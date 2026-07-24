using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed class CurrentAccountImportService
{
    private readonly AppPaths _paths;
    private readonly ProfileVault _vault;
    private readonly AccountMetadataService _metadataService;

    public CurrentAccountImportService(
        AppPaths paths,
        ProfileVault vault,
        AccountMetadataService metadataService)
    {
        _paths = paths;
        _vault = vault;
        _metadataService = metadataService;
    }

    public bool HasLiveAccount => File.Exists(_paths.LiveAuthFile);

    public async Task<AccountProfile> ImportAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.LiveAuthFile))
        {
            throw new FileNotFoundException("当前 ChatGPT 认证文件不存在。", _paths.LiveAuthFile);
        }

        var credential = await File.ReadAllBytesAsync(_paths.LiveAuthFile, cancellationToken);
        var auth = AuthDocument.Inspect(credential);
        if (!auth.HasManagedTokens)
        {
            throw new InvalidDataException("当前认证不是受支持的 ChatGPT OAuth 账号。");
        }

        var claims = JwtClaimsReader.Read(auth);
        var existing = !string.IsNullOrWhiteSpace(claims.AccountId)
            ? await _vault.FindByAccountIdAsync(claims.AccountId, cancellationToken)
            : null;
        var metadata = _metadataService.Resolve(claims, cached: existing);
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
            IsActive = true,
            MembershipPlan = metadata.MembershipPlan,
            Ownership = metadata.Ownership,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            Quota = existing?.Quota
        };
        return await _vault.UpsertProfileAsync(profile, credential, cancellationToken);
    }
}
