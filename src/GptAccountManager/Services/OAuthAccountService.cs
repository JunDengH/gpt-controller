using System.Diagnostics;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed class OAuthAccountService
{
    private readonly AppPaths _paths;
    private readonly ProfileVault _vault;
    private readonly CodexLocator _locator;
    private readonly AccountMetadataService _metadataService;
    private readonly QuotaParser _quotaParser;
    private readonly OperationGate _operationGate;
    private readonly RedactingLogger _logger;

    public OAuthAccountService(
        AppPaths paths,
        ProfileVault vault,
        CodexLocator locator,
        AccountMetadataService metadataService,
        QuotaParser quotaParser,
        OperationGate operationGate,
        RedactingLogger logger)
    {
        _paths = paths;
        _vault = vault;
        _locator = locator;
        _metadataService = metadataService;
        _quotaParser = quotaParser;
        _operationGate = operationGate;
        _logger = logger;
    }

    public async Task<AccountProfile> AddAccountAsync(
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _operationGate.EnterAsync(cancellationToken);
        var loginDirectory = Path.Combine(_paths.Temp, $"login-{Guid.NewGuid():N}");
        Directory.CreateDirectory(loginDirectory);

        try
        {
            status?.Invoke("正在启动官方登录流程…");
            var installation = await _locator.LocateAsync(cancellationToken);
            AccountReadMetadata accountRead;
            QuotaParseResult? quotaResult = null;

            await using (var client = await CodexAppServerClient.StartAsync(
                             installation.CodexExecutable,
                             loginDirectory,
                             _logger,
                             cancellationToken))
            {
                var login = await client.StartChatGptLoginAsync(cancellationToken);
                Process.Start(new ProcessStartInfo
                {
                    FileName = login.AuthUrl,
                    UseShellExecute = true
                });
                status?.Invoke("请在浏览器中完成 ChatGPT 登录…");
                await client.WaitForLoginCompletedAsync(
                    login.LoginId,
                    TimeSpan.FromMinutes(5),
                    cancellationToken);
                status?.Invoke("正在验证账号…");
                accountRead = await client.ReadAccountAsync(cancellationToken);
                try
                {
                    var limits = await client.ReadRateLimitsAsync(cancellationToken);
                    quotaResult = _quotaParser.Parse(limits, DateTimeOffset.UtcNow);
                }
                catch (Exception exception)
                {
                    await _logger.WarningAsync(
                        "oauth.quota",
                        $"Initial quota unavailable: {exception.Message}");
                }
            }

            var authPath = Path.Combine(loginDirectory, "auth.json");
            if (!File.Exists(authPath))
            {
                throw new InvalidDataException("官方登录没有生成 auth.json。");
            }

            var credential = await File.ReadAllBytesAsync(authPath, cancellationToken);
            var auth = AuthDocument.Inspect(credential);
            if (!auth.HasManagedTokens || !auth.HasRefreshToken)
            {
                throw new InvalidDataException("登录生成的认证文件不完整。");
            }

            var claims = JwtClaimsReader.Read(auth);
            var existing = !string.IsNullOrWhiteSpace(claims.AccountId)
                ? await _vault.FindByAccountIdAsync(claims.AccountId, cancellationToken)
                : null;
            var metadata = _metadataService.Resolve(
                claims,
                quotaResult?.PlanType,
                accountRead,
                existing);

            var nickname = existing?.Nickname;
            if (string.IsNullOrWhiteSpace(nickname))
            {
                nickname = metadata.Email.Split('@')[0];
            }

            var profile = new AccountProfile
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Nickname = nickname,
                Email = metadata.Email,
                AccountId = metadata.AccountId,
                IsActive = existing?.IsActive ?? false,
                MembershipPlan = metadata.MembershipPlan,
                Ownership = metadata.Ownership,
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                Quota = quotaResult?.Snapshot ?? existing?.Quota
            };

            status?.Invoke("正在加密保存账号…");
            return await _vault.UpsertProfileAsync(profile, credential, cancellationToken);
        }
        finally
        {
            QuotaService.DeleteDirectoryBestEffort(loginDirectory);
        }
    }
}
