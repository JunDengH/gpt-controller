using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class ProfileVaultTests
{
    private string _root = null!;
    private AppPaths _paths = null!;
    private ProfileVault _vault = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "gam-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root, _root);
        _paths.EnsureCreated();
        _vault = new ProfileVault(_paths);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort in test cleanup.
        }
    }

    [TestMethod]
    public async Task Upsert_EncryptsCredentialAndLoadsProfile()
    {
        var credential = TestAuthFactory.Create("account-1");
        var profile = CreateProfile("account-1", true);

        var stored = await _vault.UpsertProfileAsync(profile, credential);
        var roundTrip = await _vault.ReadCredentialAsync(stored.Id);
        var profiles = await _vault.LoadProfilesAsync();

        CollectionAssert.AreEqual(credential, roundTrip);
        Assert.AreEqual(1, profiles.Count);
        Assert.IsTrue(profiles[0].IsActive);

        var onDisk = await File.ReadAllBytesAsync(_paths.GetCredentialPath(stored.Id));
        Assert.IsFalse(onDisk.AsSpan().SequenceEqual(credential));
        Assert.IsFalse(Encoding.UTF8.GetString(onDisk).Contains("refresh-token"));
    }

    [TestMethod]
    public async Task SetActiveProfile_LeavesExactlyOneActive()
    {
        var first = await _vault.UpsertProfileAsync(
            CreateProfile("account-1", true),
            TestAuthFactory.Create("account-1"));
        var second = await _vault.UpsertProfileAsync(
            CreateProfile("account-2", false),
            TestAuthFactory.Create("account-2"));

        await _vault.SetActiveProfileAsync(second.Id);
        var profiles = await _vault.LoadProfilesAsync();

        Assert.AreEqual(1, profiles.Count(item => item.IsActive));
        Assert.AreEqual(second.Id, profiles.Single(item => item.IsActive).Id);
        Assert.IsFalse(profiles.Single(item => item.Id == first.Id).IsActive);
    }

    [TestMethod]
    public async Task Backup_RoundTripsThroughDpapi()
    {
        var credential = TestAuthFactory.Create("account-1");

        var backup = await _vault.CreateBackupAsync(credential, Guid.NewGuid());
        var restored = await _vault.ReadBackupAsync(backup);

        CollectionAssert.AreEqual(credential, restored);
    }

    private static AccountProfile CreateProfile(string accountId, bool active) =>
        new()
        {
            Nickname = accountId,
            Email = $"{accountId}@example.com",
            AccountId = accountId,
            IsActive = active
        };
}
