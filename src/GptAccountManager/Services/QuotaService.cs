using System.Collections.Concurrent;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed class QuotaService
{
    private readonly AppPaths _paths;
    private readonly ProfileVault _vault;
    private readonly CodexLocator _locator;
    private readonly IChatGptProcessController _processController;
    private readonly AccountMetadataService _metadataService;
    private readonly QuotaParser _quotaParser;
    private readonly OperationGate _operationGate;
    private readonly RedactingLogger _logger;
    private readonly ConcurrentDictionary<Guid, Task<AccountProfile>> _inflight = new();

    public QuotaService(
        AppPaths paths,
        ProfileVault vault,
        CodexLocator locator,
        IChatGptProcessController processController,
        AccountMetadataService metadataService,
        QuotaParser quotaParser,
        OperationGate operationGate,
        RedactingLogger logger)
    {
        _paths = paths;
        _vault = vault;
        _locator = locator;
        _processController = processController;
        _metadataService = metadataService;
        _quotaParser = quotaParser;
        _operationGate = operationGate;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AccountProfile>> RefreshAllAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = await _vault.LoadProfilesAsync(cancellationToken);
        var refreshed = new List<AccountProfile>();
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            refreshed.Add(await RefreshAsync(profile.Id, cancellationToken));
        }

        return refreshed;
    }

    public Task<AccountProfile> RefreshAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        return _inflight.GetOrAdd(
            profileId,
            _ => RefreshCoreAndReleaseAsync(profileId, cancellationToken));
    }

    private async Task<AccountProfile> RefreshCoreAndReleaseAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshCoreAsync(profileId, cancellationToken);
        }
        finally
        {
            _inflight.TryRemove(profileId, out _);
        }
    }

    private async Task<AccountProfile> RefreshCoreAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        using var operation = await _operationGate.EnterAsync(cancellationToken);
        var profile = await _vault.GetProfileAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("账号不存在。");
        var storedCredential = await _vault.ReadCredentialAsync(profileId, cancellationToken);

        var isActiveProbe = profile.IsActive && _processController.IsChatGptRunning();
        var sourceCredential = storedCredential;
        if (isActiveProbe && File.Exists(_paths.LiveAuthFile))
        {
            sourceCredential = await File.ReadAllBytesAsync(_paths.LiveAuthFile, cancellationToken);
        }

        AuthDocumentInfo sourceInfo;
        try
        {
            sourceInfo = AuthDocument.Inspect(sourceCredential);
            if (!sourceInfo.HasManagedTokens)
            {
                throw new InvalidDataException("认证文件缺少 ChatGPT Token。");
            }
        }
        catch (Exception exception)
        {
            return await SaveFailureAsync(
                profile,
                exception,
                QuotaStatus.AuthenticationRequired,
                "invalid_auth",
                cancellationToken);
        }

        var probeCredential = isActiveProbe
            ? AuthDocument.RemoveRefreshToken(sourceCredential)
            : sourceCredential;
        var probeDirectory = Path.Combine(_paths.Temp, $"quota-{profile.Id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);
        var probeAuthPath = Path.Combine(probeDirectory, "auth.json");
        await AtomicFile.WriteAllBytesAsync(probeAuthPath, probeCredential, cancellationToken);

        try
        {
            var installation = await _locator.LocateAsync(cancellationToken);
            var probeResult = await RetryPolicy.ExecuteAsync(
                async token =>
                {
                    await using var client = await CodexAppServerClient.StartAsync(
                        installation.CodexExecutable,
                        probeDirectory,
                        _logger,
                        token);
                    var account = await client.ReadAccountAsync(token);
                    var rateLimits = await client.ReadRateLimitsAsync(token);
                    return (
                        Account: account,
                        Quota: _quotaParser.Parse(rateLimits, DateTimeOffset.UtcNow));
                },
                IsTransientFailure,
                cancellationToken: cancellationToken);
            var accountRead = probeResult.Account;
            var quotaResult = probeResult.Quota;

            var finalProbeCredential = File.Exists(probeAuthPath)
                ? await File.ReadAllBytesAsync(probeAuthPath, cancellationToken)
                : probeCredential;
            var claims = JwtClaimsReader.Read(AuthDocument.Inspect(finalProbeCredential));
            var metadata = _metadataService.Resolve(
                claims,
                quotaResult.PlanType,
                accountRead,
                profile);

            if (!string.Equals(
                    metadata.AccountId,
                    profile.AccountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("额度响应的账号与档案不一致。");
            }

            if (!isActiveProbe &&
                !AuthDocument.SemanticallyEqual(storedCredential, finalProbeCredential))
            {
                var updatedInfo = AuthDocument.Inspect(finalProbeCredential);
                if (!sourceInfo.HasRefreshToken || updatedInfo.HasRefreshToken)
                {
                    await _vault.WriteCredentialAsync(
                        profile.Id,
                        finalProbeCredential,
                        cancellationToken);
                }
                else
                {
                    await _logger.WarningAsync(
                        "quota.capture",
                        $"Skipped incomplete rotated credential for profile {profile.Id:N}.");
                }
            }

            var updated = profile with
            {
                Email = metadata.Email,
                MembershipPlan = metadata.MembershipPlan,
                Ownership = metadata.Ownership,
                Quota = quotaResult.Snapshot,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return await _vault.UpsertProfileAsync(updated, null, cancellationToken);
        }
        catch (Exception exception)
        {
            var status = IsAuthenticationFailure(exception)
                ? QuotaStatus.AuthenticationRequired
                : QuotaStatus.Stale;
            var errorCode = status == QuotaStatus.AuthenticationRequired
                ? "authentication_required"
                : "quota_refresh_failed";
            return await SaveFailureAsync(
                profile,
                exception,
                status,
                errorCode,
                cancellationToken);
        }
        finally
        {
            DeleteDirectoryBestEffort(probeDirectory);
        }
    }

    private async Task<AccountProfile> SaveFailureAsync(
        AccountProfile profile,
        Exception exception,
        QuotaStatus status,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await _logger.ErrorAsync("quota.refresh", exception);
        var previous = profile.Quota;
        var failedQuota = previous is { RemainingPercent: not null }
            ? previous with
            {
                Status = status == QuotaStatus.AuthenticationRequired
                    ? QuotaStatus.AuthenticationRequired
                    : QuotaStatus.Stale,
                ErrorCode = errorCode
            }
            : new QuotaSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Status = status,
                ErrorCode = errorCode
            };
        var updated = profile with
        {
            Quota = failedQuota,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await _vault.UpsertProfileAsync(updated, null, cancellationToken);
    }

    private static bool IsAuthenticationFailure(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("refresh token", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("signed-in", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientFailure(Exception exception)
    {
        if (exception is TimeoutException or IOException)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("app-server exited", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
    }

    public static void DeleteDirectoryBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Startup cleanup will retry.
        }
    }
}
