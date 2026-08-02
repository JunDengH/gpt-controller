using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;

namespace GptController.Tests;

public sealed class ApplicationDataMigratorTests
{
    private const string LegacyProfileEntropy =
        "GptAccountManager/ProfileVault/v1";
    private const string CurrentProfileEntropy =
        "GptController/ProfileVault/v2";
    private const string LegacyDeepSeekEntropy =
        "GptAccountManager/DeepSeekCredential/v1";
    private const string CurrentDeepSeekEntropy =
        "GptController/DeepSeekCredential/v2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public async Task MigrateIfNeededAsync_ConvertsCompleteLegacyDataAndBuildsProjection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var harness = MigrationHarness.Create();
        var profileCredential = CreateCredential("account-1");
        var profile = await harness.AddLegacyProfileAsync(
            "account-1",
            isActive: true,
            profileCredential);
        var backupCredential = CreateCredential("backup-account");
        var backupPath = Path.Combine(
            harness.LegacyPaths.Backups,
            $"20260802010101000_{profile.Id:N}.bin");
        await WriteProtectedAsync(
            backupPath,
            backupCredential,
            LegacyProfileEntropy);

        var settings = new AppSettings
        {
            QuotaRefreshMinutes = 30,
            CloseToTray = false,
            StartMinimized = true
        };
        await WriteJsonAsync(harness.LegacyPaths.SettingsFile, settings);

        const string apiKey = "sk-legacy-deepseek-ABCD";
        await harness.AddLegacyDeepSeekAsync(apiKey, isActive: false);
        await WriteJsonAsync(
            harness.LegacyPaths.DeepSeekModelCatalogFile,
            new { models = Array.Empty<object>() });
        await WriteJsonAsync(
            harness.LegacyPaths.TransactionFile,
            new { phase = "pending" });
        await WriteJsonAsync(
            harness.LegacyPaths.DeepSeekConfigStateFile,
            new { phase = "applying" });
        await File.WriteAllTextAsync(
            Path.Combine(harness.LegacyPaths.Runtime, "legacy-runtime.bin"),
            "do not migrate");
        await File.WriteAllTextAsync(
            Path.Combine(harness.LegacyPaths.Logs, "legacy.log"),
            "do not migrate");
        await File.WriteAllTextAsync(
            Path.Combine(harness.LegacyPaths.Temp, "legacy.tmp"),
            "do not migrate");

        var legacySnapshot = await SnapshotAsync(harness.LegacyPaths.Root);
        var recoveryCalled = false;

        var result = await new ApplicationDataMigrator(harness.TargetPaths)
            .MigrateIfNeededAsync(async (stagedPaths, cancellationToken) =>
            {
                recoveryCalled = true;
                Assert.NotEqual(harness.LegacyPaths.Root, stagedPaths.Root);
                Assert.NotEqual(harness.TargetPaths.Root, stagedPaths.Root);

                var stagedCredential = await new ProfileVault(stagedPaths)
                    .ReadCredentialAsync(profile.Id, cancellationToken);
                Assert.Equal(profileCredential, stagedCredential);
                Assert.Equal(
                    apiKey,
                    await new DeepSeekCredentialStore(stagedPaths.Root)
                        .ReadAsync(cancellationToken));

                File.Delete(stagedPaths.TransactionFile);
                File.Delete(stagedPaths.DeepSeekConfigStateFile);
            });

        Assert.True(recoveryCalled);
        Assert.Equal(ApplicationDataMigrationStatus.Migrated, result.Status);
        Assert.True(Directory.Exists(harness.TargetPaths.Root));
        Assert.True(Directory.Exists(harness.LegacyPaths.Root));
        Assert.Equal(legacySnapshot, await SnapshotAsync(harness.LegacyPaths.Root));
        Assert.True(File.Exists(Path.Combine(harness.TargetPaths.Root, "migration.json")));
        Assert.False(File.Exists(harness.TargetPaths.TransactionFile));
        Assert.False(File.Exists(harness.TargetPaths.DeepSeekConfigStateFile));
        Assert.False(File.Exists(Path.Combine(
            harness.TargetPaths.Runtime,
            "legacy-runtime.bin")));
        Assert.False(File.Exists(Path.Combine(harness.TargetPaths.Logs, "legacy.log")));
        Assert.False(File.Exists(Path.Combine(harness.TargetPaths.Temp, "legacy.tmp")));

        var migratedProfiles = await new ProfileVault(harness.TargetPaths)
            .LoadProfilesAsync();
        var migratedProfile = Assert.Single(migratedProfiles);
        Assert.Equal(profile, migratedProfile);
        Assert.Equal(
            profileCredential,
            await new ProfileVault(harness.TargetPaths)
                .ReadCredentialAsync(profile.Id));

        var migratedSettings = await new SettingsService(harness.TargetPaths).LoadAsync();
        Assert.Equal(settings, migratedSettings);

        var deepSeekCredentialStore = new DeepSeekCredentialStore(
            harness.TargetPaths.Root);
        Assert.Equal(apiKey, await deepSeekCredentialStore.ReadAsync());
        var migratedMetadata = await deepSeekCredentialStore.GetMetadataAsync();
        Assert.NotNull(migratedMetadata);
        Assert.Equal(
            DeepSeekCredentialMetadata.CurrentSchemaVersion,
            migratedMetadata.SchemaVersion);
        Assert.Equal("ABCD", migratedMetadata.KeyLastFour);

        var projection = await new ConnectionIndexStore(harness.TargetPaths).LoadAsync();
        Assert.NotNull(projection);
        Assert.Equal(ConnectionIndex.CurrentSchemaVersion, projection.SchemaVersion);
        Assert.Equal(profile.Id, Assert.Single(projection.ChatGptConnections).ProfileId);
        Assert.NotNull(projection.DeepSeekConnection);
        Assert.Equal(ConnectionProvider.ChatGpt, projection.ActiveConnection?.Provider);
        Assert.Equal(profile.Id.ToString("N"), projection.ActiveConnection?.ConnectionId);

        var migratedProfileBlob = await File.ReadAllBytesAsync(
            harness.TargetPaths.GetCredentialPath(profile.Id));
        Assert.Equal(
            profileCredential,
            Unprotect(migratedProfileBlob, CurrentProfileEntropy));
        Assert.Throws<CryptographicException>(
            () => Unprotect(migratedProfileBlob, LegacyProfileEntropy));

        var migratedBackupBlob = await File.ReadAllBytesAsync(
            Path.Combine(harness.TargetPaths.Backups, Path.GetFileName(backupPath)));
        Assert.Equal(
            backupCredential,
            Unprotect(migratedBackupBlob, CurrentProfileEntropy));
        Assert.Throws<CryptographicException>(
            () => Unprotect(migratedBackupBlob, LegacyProfileEntropy));

        var migratedDeepSeekBlob = await File.ReadAllBytesAsync(Path.Combine(
            harness.TargetPaths.Root,
            "credentials",
            "deepseek",
            migratedMetadata.CredentialFile));
        Assert.Equal(
            apiKey,
            Encoding.UTF8.GetString(
                Unprotect(migratedDeepSeekBlob, CurrentDeepSeekEntropy)));
        Assert.Throws<CryptographicException>(
            () => Unprotect(migratedDeepSeekBlob, LegacyDeepSeekEntropy));

        var legacyProfileBlob = await File.ReadAllBytesAsync(
            harness.LegacyPaths.GetCredentialPath(profile.Id));
        Assert.Equal(
            profileCredential,
            Unprotect(legacyProfileBlob, LegacyProfileEntropy));

        foreach (var textFile in Directory.EnumerateFiles(
                     harness.TargetPaths.Root,
                     "*.json",
                     SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(
                apiKey,
                await File.ReadAllTextAsync(textFile),
                StringComparison.Ordinal);
        }

        Assert.Empty(harness.EnumerateStagingDirectories());
    }

    [Fact]
    public async Task MigrateIfNeededAsync_CorruptedLegacyCredentialBlocksActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var harness = MigrationHarness.Create();
        var profile = await harness.AddLegacyProfileAsync(
            "account-corrupt",
            isActive: true,
            CreateCredential("account-corrupt"));
        await File.WriteAllBytesAsync(
            harness.LegacyPaths.GetCredentialPath(profile.Id),
            [0x01, 0x02, 0x03, 0x04]);
        var legacySnapshot = await SnapshotAsync(harness.LegacyPaths.Root);

        await Assert.ThrowsAsync<ApplicationDataMigrationException>(
            () => new ApplicationDataMigrator(harness.TargetPaths)
                .MigrateIfNeededAsync());

        Assert.False(Directory.Exists(harness.TargetPaths.Root));
        Assert.Equal(legacySnapshot, await SnapshotAsync(harness.LegacyPaths.Root));
        Assert.Empty(harness.EnumerateStagingDirectories());
    }

    [Fact]
    public async Task MigrateIfNeededAsync_FailedStagedRecoveryLeavesLegacyUntouchedAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var harness = MigrationHarness.Create();
        var profileCredential = CreateCredential("account-retry");
        await harness.AddLegacyProfileAsync(
            "account-retry",
            isActive: true,
            profileCredential);
        await WriteJsonAsync(
            harness.LegacyPaths.TransactionFile,
            new { phase = "pending" });
        var legacySnapshot = await SnapshotAsync(harness.LegacyPaths.Root);

        await Assert.ThrowsAsync<ApplicationDataMigrationException>(
            () => new ApplicationDataMigrator(harness.TargetPaths)
                .MigrateIfNeededAsync((stagedPaths, _) =>
                {
                    File.Delete(stagedPaths.TransactionFile);
                    File.WriteAllText(
                        Path.Combine(stagedPaths.Root, "partial-write.txt"),
                        "staging only");
                    throw new IOException("Injected staged recovery failure.");
                }));

        Assert.False(Directory.Exists(harness.TargetPaths.Root));
        Assert.Equal(legacySnapshot, await SnapshotAsync(harness.LegacyPaths.Root));
        Assert.Empty(harness.EnumerateStagingDirectories());

        var retry = await new ApplicationDataMigrator(harness.TargetPaths)
            .MigrateIfNeededAsync((stagedPaths, _) =>
            {
                File.Delete(stagedPaths.TransactionFile);
                return Task.CompletedTask;
            });

        Assert.Equal(ApplicationDataMigrationStatus.Migrated, retry.Status);
        Assert.Equal(legacySnapshot, await SnapshotAsync(harness.LegacyPaths.Root));
        Assert.Equal(
            profileCredential,
            await new ProfileVault(harness.TargetPaths)
                .ReadCredentialAsync(
                    (await new ProfileVault(harness.TargetPaths)
                        .LoadProfilesAsync()).Single().Id));
        Assert.Empty(harness.EnumerateStagingDirectories());
    }

    private static byte[] CreateCredential(string accountId) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new
            {
                id_token = "id-token",
                access_token = "access-token",
                refresh_token = "refresh-token",
                account_id = accountId
            }
        });

    private static async Task WriteProtectedAsync(
        string path,
        byte[] clear,
        string entropyPurpose)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Protected file has no directory."));
        var protectedBytes = ProtectedData.Protect(
            clear,
            Encoding.UTF8.GetBytes(entropyPurpose),
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(path, protectedBytes);
    }

    private static byte[] Unprotect(byte[] protectedBytes, string entropyPurpose) =>
        ProtectedData.Unprotect(
            protectedBytes,
            Encoding.UTF8.GetBytes(entropyPurpose),
            DataProtectionScope.CurrentUser);

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("JSON file has no directory."));
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
    }

    private static async Task<string[]> SnapshotAsync(string root)
    {
        var snapshot = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            snapshot.Add(
                $"{Path.GetRelativePath(root, path)}={Convert.ToHexString(SHA256.HashData(bytes))}");
        }

        return snapshot.ToArray();
    }

    private sealed class MigrationHarness : IAsyncDisposable
    {
        private MigrationHarness(string root)
        {
            Root = root;
            LocalAppData = Path.Combine(root, "local");
            UserProfile = Path.Combine(root, "profile");
            TargetPaths = new AppPaths(LocalAppData, UserProfile);
            LegacyPaths = TargetPaths.CreateSibling(
                ApplicationDataLayout.LegacyApplicationDirectoryName);
            LegacyPaths.EnsureCreated();
        }

        public string Root { get; }
        public string LocalAppData { get; }
        public string UserProfile { get; }
        public AppPaths TargetPaths { get; }
        public AppPaths LegacyPaths { get; }

        public static MigrationHarness Create() => new(Path.Combine(
            Path.GetTempPath(),
            "GptController.Tests",
            Guid.NewGuid().ToString("N")));

        public async Task<AccountProfile> AddLegacyProfileAsync(
            string accountId,
            bool isActive,
            byte[] credential)
        {
            var profile = new AccountProfile
            {
                Id = Guid.NewGuid(),
                Nickname = $"Profile {accountId}",
                Email = $"{accountId}@example.com",
                AccountId = accountId,
                IsActive = isActive,
                MembershipPlan = MembershipPlan.Plus,
                Ownership = AccountOwnership.Personal,
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z")
            };
            await WriteJsonAsync(LegacyPaths.IndexFile, new[] { profile });
            await WriteProtectedAsync(
                LegacyPaths.GetCredentialPath(profile.Id),
                credential,
                LegacyProfileEntropy);
            return profile;
        }

        public async Task AddLegacyDeepSeekAsync(string apiKey, bool isActive)
        {
            const string credentialFile = "legacy-deepseek.bin";
            await WriteProtectedAsync(
                Path.Combine(
                    LegacyPaths.Root,
                    "credentials",
                    "deepseek",
                    credentialFile),
                Encoding.UTF8.GetBytes(apiKey),
                LegacyDeepSeekEntropy);
            await WriteJsonAsync(
                Path.Combine(LegacyPaths.Connections, "deepseek-credential.json"),
                new DeepSeekCredentialMetadata
                {
                    SchemaVersion = 1,
                    Provider = ApplicationDataLayout.DeepSeekProvider,
                    Model = ApplicationDataLayout.DeepSeekModel,
                    KeyLastFour = apiKey[^4..],
                    CredentialFile = credentialFile,
                    CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z")
                });
            await WriteJsonAsync(
                Path.Combine(LegacyPaths.Connections, "deepseek.json"),
                new DeepSeekConnection
                {
                    Nickname = "DeepSeek Legacy",
                    KeyLastFour = apiKey[^4..],
                    IsActive = isActive,
                    Status = DeepSeekConnectionStatus.Available,
                    CnyBalance = 10.5m,
                    UsdBalance = 1.5m
                });
        }

        public IEnumerable<string> EnumerateStagingDirectories() =>
            Directory.Exists(LocalAppData)
                ? Directory.EnumerateDirectories(
                    LocalAppData,
                    ".GptController.migrating-*",
                    SearchOption.TopDirectoryOnly)
                : [];

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Test cleanup is best effort on Windows where DPAPI files can be scanned briefly.
            }

            return ValueTask.CompletedTask;
        }
    }
}
