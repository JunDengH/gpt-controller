using System.Collections.Concurrent;
using System.Diagnostics;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

internal static class QuotaRefreshPolicy
{
    public static readonly TimeSpan ActiveAccessTokenMinimumLifetime =
        TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FastFailureMaximumDuration =
        TimeSpan.FromSeconds(2);

    public static bool CanProbeActiveAccount(
        DateTimeOffset? accessTokenExpiresAt,
        DateTimeOffset now) =>
        accessTokenExpiresAt is { } expiration &&
        expiration - now >= ActiveAccessTokenMinimumLifetime;

    public static bool ShouldSkipAutomatic(QuotaSnapshot? quota) =>
        quota?.Status == QuotaStatus.AuthenticationRequired &&
        quota.ErrorCode is "invalid_auth" or "confirmed_unauthorized";

    public static bool IsConfirmedAuthenticationFailure(Exception exception)
    {
        if (exception is CodexAppServerException appServerException &&
            appServerException.ErrorCode is { } code &&
            (string.Equals(code, "401", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(code, "unauthorized", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var message = exception.Message;
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mentionsRefreshToken =
            message.Contains("refresh token", StringComparison.OrdinalIgnoreCase);
        var explicitlyRejected =
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("revoked", StringComparison.OrdinalIgnoreCase);
        return mentionsRefreshToken && explicitlyRejected;
    }

    public static bool IsFastTransientFailure(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return false;
        }

        if (exception is IOException)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("app-server exited", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldRetryFastTransient(
        Exception exception,
        TimeSpan elapsed) =>
        elapsed <= FastFailureMaximumDuration &&
        IsFastTransientFailure(exception);
}

public sealed class QuotaService
{
    private readonly AppPaths _paths;
    private readonly ProfileVault _vault;
    private readonly ICodexRuntimeLocator _locator;
    private readonly ICodexAppServerClientFactory _appServerFactory;
    private readonly IChatGptProcessController _processController;
    private readonly AccountMetadataService _metadataService;
    private readonly QuotaParser _quotaParser;
    private readonly OperationGate _operationGate;
    private readonly RedactingLogger _logger;
    private readonly ConcurrentDictionary<Guid, Task<AccountProfile>> _inflight = new();

    public QuotaService(
        AppPaths paths,
        ProfileVault vault,
        ICodexRuntimeLocator locator,
        ICodexAppServerClientFactory appServerFactory,
        IChatGptProcessController processController,
        AccountMetadataService metadataService,
        QuotaParser quotaParser,
        OperationGate operationGate,
        RedactingLogger logger)
    {
        _paths = paths;
        _vault = vault;
        _locator = locator;
        _appServerFactory = appServerFactory;
        _processController = processController;
        _metadataService = metadataService;
        _quotaParser = quotaParser;
        _operationGate = operationGate;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AccountProfile>> RefreshAllAsync(
        QuotaRefreshReason reason = QuotaRefreshReason.Manual,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _vault.LoadProfilesAsync(cancellationToken);
        var refreshed = new List<AccountProfile>();
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reason == QuotaRefreshReason.Automatic &&
                QuotaRefreshPolicy.ShouldSkipAutomatic(profile.Quota))
            {
                continue;
            }

            refreshed.Add(await RefreshAsync(profile.Id, reason, cancellationToken));
        }

        return refreshed;
    }

    public Task<AccountProfile> RefreshAsync(
        Guid profileId,
        QuotaRefreshReason reason = QuotaRefreshReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return _inflight.GetOrAdd(
            profileId,
            _ => RefreshCoreAndReleaseAsync(profileId, reason, cancellationToken));
    }

    private async Task<AccountProfile> RefreshCoreAndReleaseAsync(
        Guid profileId,
        QuotaRefreshReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshCoreAsync(profileId, reason, cancellationToken);
        }
        finally
        {
            _inflight.TryRemove(profileId, out _);
        }
    }

    private async Task<AccountProfile> RefreshCoreAsync(
        Guid profileId,
        QuotaRefreshReason reason,
        CancellationToken cancellationToken)
    {
        using var operation = await _operationGate.EnterAsync(cancellationToken);
        var profile = await _vault.GetProfileAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("账号不存在。");

        byte[] storedCredential;
        try
        {
            storedCredential = await _vault.ReadCredentialAsync(profileId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await SaveFailureAsync(
                profile,
                exception,
                QuotaStatus.AuthenticationRequired,
                "invalid_auth",
                cancellationToken);
        }

        var isActiveProbe = profile.IsActive && _processController.IsChatGptRunning();
        var sourceCredential = storedCredential;
        if (isActiveProbe && File.Exists(_paths.LiveAuthFile))
        {
            sourceCredential = await File.ReadAllBytesAsync(
                _paths.LiveAuthFile,
                cancellationToken);
        }

        AuthDocumentInfo sourceInfo;
        AuthClaims sourceClaims;
        try
        {
            sourceInfo = AuthDocument.Inspect(sourceCredential);
            if (!sourceInfo.HasManagedTokens || !sourceInfo.HasRefreshToken)
            {
                throw new InvalidDataException("认证文件缺少完整的 ChatGPT Token。");
            }

            sourceClaims = JwtClaimsReader.Read(sourceInfo);
            if (!CredentialMatchesProfile(sourceClaims, profile.AccountId))
            {
                throw new InvalidDataException("认证文件的账号与档案不一致。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await SaveFailureAsync(
                profile,
                exception,
                QuotaStatus.AuthenticationRequired,
                "invalid_auth",
                cancellationToken);
        }

        if (isActiveProbe &&
            !QuotaRefreshPolicy.CanProbeActiveAccount(
                sourceClaims.AccessTokenExpiresAt,
                DateTimeOffset.UtcNow))
        {
            return await SaveFailureAsync(
                profile,
                null,
                QuotaStatus.Stale,
                "active_refresh_deferred",
                cancellationToken);
        }

        var probeDirectory = Path.Combine(
            _paths.Temp,
            $"quota-{profile.Id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);
        var probeAuthPath = Path.Combine(probeDirectory, "auth.json");
        await AtomicFile.WriteAllBytesAsync(
            probeAuthPath,
            sourceCredential,
            cancellationToken);

        try
        {
            var installation = await _locator.LocateAsync(cancellationToken);
            var attemptTimer = new Stopwatch();
            var probeResult = await RetryPolicy.ExecuteAsync(
                async token =>
                {
                    attemptTimer.Restart();
                    await using var client = await _appServerFactory.StartAsync(
                        installation.CodexExecutable,
                        probeDirectory,
                        _logger,
                        token);
                    var account = await client.ReadAccountAsync(token);
                    var rateLimits = await client.ReadRateLimitsAsync(token);
                    return (
                        Account: account,
                        Quota: _quotaParser.Parse(
                            rateLimits,
                            DateTimeOffset.UtcNow));
                },
                exception => QuotaRefreshPolicy.ShouldRetryFastTransient(
                    exception,
                    attemptTimer.Elapsed),
                maxAttempts: 2,
                initialDelay: TimeSpan.FromMilliseconds(300),
                cancellationToken: cancellationToken);
            var accountRead = probeResult.Account;
            var quotaResult = probeResult.Quota;

            var finalProbeCredential = File.Exists(probeAuthPath)
                ? await File.ReadAllBytesAsync(probeAuthPath, cancellationToken)
                : sourceCredential;
            var finalInfo = AuthDocument.Inspect(finalProbeCredential);
            var finalClaims = JwtClaimsReader.Read(finalInfo);
            if (!finalInfo.HasManagedTokens ||
                !finalInfo.HasRefreshToken ||
                !CredentialMatchesProfile(finalClaims, profile.AccountId))
            {
                throw new InvalidDataException("额度探测生成了不完整或不匹配的认证文件。");
            }

            var metadata = _metadataService.Resolve(
                finalClaims,
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

            if (!AuthDocument.SemanticallyEqual(
                    sourceCredential,
                    finalProbeCredential))
            {
                if (isActiveProbe)
                {
                    await CaptureActiveCredentialRotationAsync(
                        profile,
                        sourceCredential,
                        finalProbeCredential,
                        cancellationToken);
                }
                else
                {
                    await _vault.WriteCredentialAsync(
                        profile.Id,
                        finalProbeCredential,
                        cancellationToken);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var authenticationRequired =
                QuotaRefreshPolicy.IsConfirmedAuthenticationFailure(exception);
            return await SaveFailureAsync(
                profile,
                exception,
                authenticationRequired
                    ? QuotaStatus.AuthenticationRequired
                    : QuotaStatus.Stale,
                authenticationRequired
                    ? "confirmed_unauthorized"
                    : "quota_refresh_failed",
                cancellationToken);
        }
        finally
        {
            DeleteDirectoryBestEffort(probeDirectory);
        }
    }

    private async Task CaptureActiveCredentialRotationAsync(
        AccountProfile profile,
        byte[] sourceCredential,
        byte[] rotatedCredential,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.LiveAuthFile))
        {
            await _logger.WarningAsync(
                "quota.capture",
                $"Live credential disappeared during refresh for profile {profile.Id:N}.");
            return;
        }

        var currentLive = await File.ReadAllBytesAsync(
            _paths.LiveAuthFile,
            cancellationToken);
        if (AuthDocument.SemanticallyEqual(sourceCredential, currentLive))
        {
            await AtomicFile.WriteAllBytesAsync(
                _paths.LiveAuthFile,
                rotatedCredential,
                cancellationToken);
            await _vault.WriteCredentialAsync(
                profile.Id,
                rotatedCredential,
                cancellationToken);
            await _logger.WarningAsync(
                "quota.capture",
                $"Captured an unexpected active credential rotation for profile {profile.Id:N}.");
            return;
        }

        try
        {
            var currentInfo = AuthDocument.Inspect(currentLive);
            var currentClaims = JwtClaimsReader.Read(currentInfo);
            if (currentInfo.HasManagedTokens &&
                currentInfo.HasRefreshToken &&
                CredentialMatchesProfile(currentClaims, profile.AccountId))
            {
                await _vault.WriteCredentialAsync(
                    profile.Id,
                    currentLive,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.WarningAsync(
                "quota.capture",
                $"Could not capture the concurrently updated live credential: {exception.Message}");
        }

        await _logger.WarningAsync(
            "quota.capture",
            $"Skipped a stale active credential rotation for profile {profile.Id:N}.");
    }

    private async Task<AccountProfile> SaveFailureAsync(
        AccountProfile profile,
        Exception? exception,
        QuotaStatus status,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (exception is not null)
        {
            await _logger.ErrorAsync("quota.refresh", exception);
        }

        var previous = profile.Quota;
        var failedQuota = previous is
            { RemainingPercent: not null } or
            { FiveHourRemainingPercent: not null }
            ? previous with
            {
                Status = status,
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

    private static bool CredentialMatchesProfile(
        AuthClaims claims,
        string expectedAccountId) =>
        !string.IsNullOrWhiteSpace(claims.AccountId) &&
        string.Equals(
            claims.AccountId,
            expectedAccountId,
            StringComparison.OrdinalIgnoreCase);

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
