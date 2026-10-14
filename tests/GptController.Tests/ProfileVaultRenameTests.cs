using System.Text;
using GptController.Infrastructure;
using GptController.Models;

namespace GptController.Tests;

public sealed class ProfileVaultRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"profile-rename-{Guid.NewGuid():N}");

    [Fact]
    public async Task RenamePersistsAndPreservesCredentialsAndLatestAccountMetadata()
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var vault = new ProfileVault(paths);
        var credential = Encoding.UTF8.GetBytes("rename-test-credential");
        var profile = await vault.UpsertProfileAsync(new AccountProfile
        {
            Nickname = "旧名称",
            Email = "test@example.com",
            AccountId = "account",
            IsActive = true,
            MembershipPlan = MembershipPlan.Business,
            Ownership = AccountOwnership.Organization("account", "团队")
        }, credential);
        var encryptedBefore = await File.ReadAllBytesAsync(paths.GetCredentialPath(profile.Id));
        var quota = new QuotaSnapshot
        {
            FiveHourRemainingPercent = 83,
            FiveHourResetsAt = DateTimeOffset.UtcNow.AddHours(2),
            Status = QuotaStatus.Fresh
        };
        var refreshed = await vault.UpsertProfileAsync(profile with { Quota = quota });

        var renamed = await vault.RenameProfileAsync(profile.Id, "  新名称  ");
        var reloaded = await new ProfileVault(paths).GetProfileAsync(profile.Id);

        Assert.Equal("新名称", reloaded!.Nickname);
        Assert.Equal(renamed, reloaded);
        Assert.Equal(refreshed with { Nickname = renamed.Nickname, UpdatedAt = renamed.UpdatedAt }, renamed);
        Assert.Equal(encryptedBefore, await File.ReadAllBytesAsync(paths.GetCredentialPath(profile.Id)));
        Assert.Equal(credential, await vault.ReadCredentialAsync(profile.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("12345678901234567890123456789012345678901")]
    public async Task InvalidNameDoesNotModifyTheStoredProfile(string nickname)
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var vault = new ProfileVault(paths);
        var profile = await vault.UpsertProfileAsync(new AccountProfile
        {
            Nickname = "原名称", Email = "test@example.com", AccountId = "account"
        });

        await Assert.ThrowsAsync<ArgumentException>(() => vault.RenameProfileAsync(profile.Id, nickname));
        Assert.Equal(profile, await vault.GetProfileAsync(profile.Id));
    }

    [Fact]
    public async Task MissingAccountCannotBeRecreatedByRename()
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var vault = new ProfileVault(paths);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.RenameProfileAsync(Guid.NewGuid(), "名称"));
        Assert.Empty(await vault.LoadProfilesAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
