using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GptController.Credentials;
using GptController.Models;
using GptController.Services;

namespace GptController.Infrastructure;

public enum ApplicationDataMigrationStatus
{
    NotNeeded,
    AlreadyMigrated,
    Migrated
}

public sealed record ApplicationDataMigrationResult(
    ApplicationDataMigrationStatus Status,
    string LegacyRoot,
    string TargetRoot);

public sealed class ApplicationDataMigrationException : Exception
{
    public ApplicationDataMigrationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class ApplicationDataMigrator
{
    private const int MarkerSchemaVersion = 1;
    private const string MarkerFileName = "migration.json";
    private const string StagingPrefix = ".GptController.migrating-";

    private static readonly byte[] LegacyProfileEntropy = Encoding.UTF8.GetBytes(
        ProfileVault.LegacyCredentialEntropyPurpose);
    private static readonly byte[] CurrentProfileEntropy = Encoding.UTF8.GetBytes(
        ProfileVault.CredentialEntropyPurpose);
    private static readonly byte[] LegacyDeepSeekEntropy = Encoding.UTF8.GetBytes(
        ApplicationDataLayout.LegacyDeepSeekCredentialEntropyPurpose);
    private static readonly byte[] CurrentDeepSeekEntropy = Encoding.UTF8.GetBytes(
        ApplicationDataLayout.DeepSeekCredentialEntropyPurpose);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppPaths _targetPaths;
    private readonly AppPaths _legacyPaths;

    public ApplicationDataMigrator(AppPaths targetPaths)
    {
        _targetPaths = targetPaths;
        if (!string.Equals(
                targetPaths.ApplicationDirectoryName,
                ApplicationDataLayout.ApplicationDirectoryName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The migration target must use the GPT Controller data directory.",
                nameof(targetPaths));
        }

        _legacyPaths = targetPaths.CreateSibling(
            ApplicationDataLayout.LegacyApplicationDirectoryName);
    }

    public async Task<ApplicationDataMigrationResult> MigrateIfNeededAsync(
        Func<AppPaths, CancellationToken, Task>? recoverStagedState = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_targetPaths.Root))
        {
            if (Directory.Exists(_legacyPaths.Root) &&
                !File.Exists(GetMarkerPath(_targetPaths)))
            {
                throw new ApplicationDataMigrationException(
                    "检测到未完成的 GPT Controller 数据目录。为保护旧数据，启动已停止。");
            }

            if (File.Exists(GetMarkerPath(_targetPaths)))
            {
                await ValidateRootAsync(_targetPaths, requireSettledTransactions: true, cancellationToken);
            }

            return new(
                ApplicationDataMigrationStatus.AlreadyMigrated,
                _legacyPaths.Root,
                _targetPaths.Root);
        }

        if (!Directory.Exists(_legacyPaths.Root))
        {
            return new(
                ApplicationDataMigrationStatus.NotNeeded,
                _legacyPaths.Root,
                _targetPaths.Root);
        }

        PruneStaleStagingDirectoriesBestEffort();
        var stagingName = $"{StagingPrefix}{Guid.NewGuid():N}";
        var stagingPaths = _targetPaths.CreateSibling(stagingName);
        var movedToTarget = false;
        try
        {
            stagingPaths.EnsureCreated();
            await CopyAndConvertAsync(stagingPaths, cancellationToken);
            if (recoverStagedState is not null)
            {
                await recoverStagedState(stagingPaths, cancellationToken);
            }

            await FinalizeStagingAsync(stagingPaths, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(_targetPaths.Root))
            {
                throw new IOException(
                    "The GPT Controller data directory appeared during migration.");
            }

            Directory.Move(stagingPaths.Root, _targetPaths.Root);
            movedToTarget = true;
            try
            {
                await ValidateRootAsync(
                    _targetPaths,
                    requireSettledTransactions: true,
                    cancellationToken);
            }
            catch
            {
                if (!Directory.Exists(stagingPaths.Root) &&
                    Directory.Exists(_targetPaths.Root))
                {
                    Directory.Move(_targetPaths.Root, stagingPaths.Root);
                    movedToTarget = false;
                }

                throw;
            }

            return new(
                ApplicationDataMigrationStatus.Migrated,
                _legacyPaths.Root,
                _targetPaths.Root);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApplicationDataMigrationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ApplicationDataMigrationException(
                "旧版数据迁移失败。旧目录已保留且不会被自动删除。",
                exception);
        }
        finally
        {
            if (!movedToTarget)
            {
                TryDeleteStagingDirectory(stagingPaths.Root);
            }
        }
    }

    private async Task CopyAndConvertAsync(
        AppPaths stagingPaths,
        CancellationToken cancellationToken)
    {
        var profiles = await ReadProfilesAsync(_legacyPaths.IndexFile, cancellationToken);
        ValidateProfiles(profiles);
        await WriteJsonAsync(stagingPaths.IndexFile, profiles, cancellationToken);

        if (File.Exists(_legacyPaths.SettingsFile))
        {
            var settings = await ReadJsonAsync<AppSettings>(
                _legacyPaths.SettingsFile,
                cancellationToken) ?? throw new InvalidDataException(
                "The legacy settings file is empty.");
            await WriteJsonAsync(stagingPaths.SettingsFile, settings, cancellationToken);
        }

        foreach (var profile in profiles)
        {
            var source = _legacyPaths.GetCredentialPath(profile.Id);
            var target = stagingPaths.GetCredentialPath(profile.Id);
            var clear = await ConvertProtectedFileAsync(
                source,
                target,
                LegacyProfileEntropy,
                CurrentProfileEntropy,
                cancellationToken);
            try
            {
                var info = AuthDocument.Inspect(clear);
                if (!info.HasManagedTokens ||
                    !string.Equals(
                        info.StoredAccountId,
                        profile.AccountId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A legacy ChatGPT credential does not match its profile.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        if (Directory.Exists(_legacyPaths.Backups))
        {
            foreach (var backup in Directory.EnumerateFiles(
                         _legacyPaths.Backups,
                         "*.bin",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(stagingPaths.Backups, Path.GetFileName(backup));
                var clear = await ConvertProtectedFileAsync(
                    backup,
                    target,
                    LegacyProfileEntropy,
                    CurrentProfileEntropy,
                    cancellationToken);
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        await MigrateDeepSeekAsync(stagingPaths, cancellationToken);
        await CopyJsonIfPresentAsync(
            _legacyPaths.DeepSeekModelCatalogFile,
            stagingPaths.DeepSeekModelCatalogFile,
            cancellationToken);
        await CopyJsonIfPresentAsync(
            _legacyPaths.DeepSeekConfigStateFile,
            stagingPaths.DeepSeekConfigStateFile,
            cancellationToken);
        await CopyJsonIfPresentAsync(
            _legacyPaths.TransactionFile,
            stagingPaths.TransactionFile,
            cancellationToken);
    }

    private async Task MigrateDeepSeekAsync(
        AppPaths stagingPaths,
        CancellationToken cancellationToken)
    {
        var legacyConnectionPath = Path.Combine(
            _legacyPaths.Connections,
            "deepseek.json");
        var legacyMetadataPath = Path.Combine(
            _legacyPaths.Connections,
            "deepseek-credential.json");
        var hasConnection = File.Exists(legacyConnectionPath);
        var hasMetadata = File.Exists(legacyMetadataPath);
        if (!hasConnection && !hasMetadata)
        {
            return;
        }

        if (!hasConnection || !hasMetadata)
        {
            throw new InvalidDataException(
                "The legacy DeepSeek connection and credential metadata are incomplete.");
        }

        var connection = await ReadJsonAsync<DeepSeekConnection>(
            legacyConnectionPath,
            cancellationToken) ?? throw new InvalidDataException(
            "The legacy DeepSeek connection is empty.");
        var metadata = await ReadJsonAsync<DeepSeekCredentialMetadata>(
            legacyMetadataPath,
            cancellationToken) ?? throw new InvalidDataException(
            "The legacy DeepSeek credential metadata is empty.");
        if (!string.Equals(connection.Id, DeepSeekConnection.FixedId, StringComparison.Ordinal) ||
            !string.Equals(connection.Model, DeepSeekDefaults.Model, StringComparison.Ordinal) ||
            metadata.SchemaVersion is < 1 or > DeepSeekCredentialMetadata.CurrentSchemaVersion ||
            metadata.KeyLastFour is not { Length: 4 } ||
            !IsSafeCredentialFile(metadata.CredentialFile))
        {
            throw new InvalidDataException("The legacy DeepSeek metadata is invalid.");
        }

        var legacyCredentialPath = Path.Combine(
            _legacyPaths.Root,
            "credentials",
            "deepseek",
            metadata.CredentialFile);
        var targetCredentialDirectory = Path.Combine(
            stagingPaths.Root,
            "credentials",
            "deepseek");
        Directory.CreateDirectory(targetCredentialDirectory);
        var targetCredentialPath = Path.Combine(
            targetCredentialDirectory,
            metadata.CredentialFile);
        var clear = await ConvertProtectedFileAsync(
            legacyCredentialPath,
            targetCredentialPath,
            LegacyDeepSeekEntropy,
            CurrentDeepSeekEntropy,
            cancellationToken);
        try
        {
            var apiKey = Encoding.UTF8.GetString(clear);
            if (string.IsNullOrWhiteSpace(apiKey) ||
                apiKey.Length < 4 ||
                !string.Equals(apiKey[^4..], metadata.KeyLastFour, StringComparison.Ordinal) ||
                apiKey.Any(character => character is < '!' or > '~'))
            {
                throw new InvalidDataException(
                    "The legacy DeepSeek credential does not match its metadata.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }

        var migratedMetadata = metadata with
        {
            SchemaVersion = DeepSeekCredentialMetadata.CurrentSchemaVersion,
            Provider = ApplicationDataLayout.DeepSeekProvider,
            Model = ApplicationDataLayout.DeepSeekModel
        };
        await WriteJsonAsync(
            Path.Combine(stagingPaths.Connections, "deepseek-credential.json"),
            migratedMetadata,
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(stagingPaths.Connections, "deepseek.json"),
            connection with { KeyLastFour = metadata.KeyLastFour },
            cancellationToken);
    }

    private async Task FinalizeStagingAsync(
        AppPaths stagingPaths,
        CancellationToken cancellationToken)
    {
        await ValidateRootAsync(
            stagingPaths,
            requireSettledTransactions: true,
            cancellationToken);
        var profiles = await new ProfileVault(stagingPaths).LoadProfilesAsync(cancellationToken);
        var deepSeekStore = new DeepSeekConnectionStore(
            stagingPaths,
            new DeepSeekCredentialStore(stagingPaths.Root));
        var deepSeek = await deepSeekStore.GetAsync(cancellationToken);
        await new ConnectionIndexStore(stagingPaths).SaveProjectionAsync(
            profiles,
            deepSeek,
            cancellationToken);
        await WriteJsonAsync(
            GetMarkerPath(stagingPaths),
            new MigrationMarker(
                MarkerSchemaVersion,
                ApplicationDataLayout.LegacyApplicationDirectoryName,
                DateTimeOffset.UtcNow),
            cancellationToken);
        await ValidateRootAsync(
            stagingPaths,
            requireSettledTransactions: true,
            cancellationToken);
    }

    private static async Task ValidateRootAsync(
        AppPaths paths,
        bool requireSettledTransactions,
        CancellationToken cancellationToken)
    {
        if (requireSettledTransactions &&
            (File.Exists(paths.TransactionFile) ||
             File.Exists(paths.DeepSeekConfigStateFile)))
        {
            throw new InvalidDataException(
                "An authentication or provider transaction is still pending.");
        }

        var vault = new ProfileVault(paths);
        var profiles = await vault.LoadProfilesAsync(cancellationToken);
        ValidateProfiles(profiles);
        foreach (var profile in profiles)
        {
            var clear = await vault.ReadCredentialAsync(profile.Id, cancellationToken);
            try
            {
                var info = AuthDocument.Inspect(clear);
                if (!info.HasManagedTokens ||
                    !string.Equals(
                        info.StoredAccountId,
                        profile.AccountId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A migrated ChatGPT credential does not match its profile.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        if (File.Exists(paths.SettingsFile))
        {
            _ = await ReadJsonAsync<AppSettings>(paths.SettingsFile, cancellationToken)
                ?? throw new InvalidDataException("The migrated settings file is empty.");
        }

        var deepSeekConnectionPath = Path.Combine(paths.Connections, "deepseek.json");
        var deepSeekMetadataPath = Path.Combine(
            paths.Connections,
            "deepseek-credential.json");
        if (File.Exists(deepSeekConnectionPath) != File.Exists(deepSeekMetadataPath))
        {
            throw new InvalidDataException("The migrated DeepSeek data is incomplete.");
        }

        if (File.Exists(deepSeekConnectionPath))
        {
            var credentialStore = new DeepSeekCredentialStore(paths.Root);
            var connectionStore = new DeepSeekConnectionStore(paths, credentialStore);
            var connection = await connectionStore.GetAsync(cancellationToken)
                ?? throw new InvalidDataException("The migrated DeepSeek connection is missing.");
            var apiKey = await credentialStore.ReadAsync(cancellationToken);
            if (apiKey.Length < 4 ||
                !string.Equals(apiKey[^4..], connection.KeyLastFour, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The migrated DeepSeek credential does not match its connection.");
            }
        }

        if (File.Exists(paths.ConnectionIndexFile))
        {
            _ = await new ConnectionIndexStore(paths).LoadAsync(cancellationToken)
                ?? throw new InvalidDataException("The migrated connection index is missing.");
        }

        if (File.Exists(paths.DeepSeekModelCatalogFile))
        {
            using var _ = JsonDocument.Parse(
                await File.ReadAllBytesAsync(
                    paths.DeepSeekModelCatalogFile,
                    cancellationToken));
        }

        var markerPath = GetMarkerPath(paths);
        if (File.Exists(markerPath))
        {
            var marker = await ReadJsonAsync<MigrationMarker>(markerPath, cancellationToken);
            if (marker is null || marker.SchemaVersion != MarkerSchemaVersion)
            {
                throw new InvalidDataException("The migration marker is invalid.");
            }
        }
    }

    private static async Task<IReadOnlyList<AccountProfile>> ReadProfilesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return await ReadJsonAsync<List<AccountProfile>>(path, cancellationToken)
               ?? throw new InvalidDataException("The legacy account index is empty.");
    }

    private static void ValidateProfiles(IReadOnlyList<AccountProfile> profiles)
    {
        if (profiles.Any(profile =>
                profile.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(profile.AccountId) ||
                string.IsNullOrWhiteSpace(profile.Nickname)) ||
            profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Count ||
            profiles.Select(profile => profile.AccountId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count ||
            profiles.Count(profile => profile.IsActive) > 1)
        {
            throw new InvalidDataException("The account index is invalid.");
        }
    }

    private static async Task<byte[]> ConvertProtectedFileAsync(
        string sourcePath,
        string targetPath,
        byte[] legacyEntropy,
        byte[] currentEntropy,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("A legacy credential file is missing.", sourcePath);
        }

        var legacyEncrypted = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        byte[]? clear = null;
        byte[]? migratedEncrypted = null;
        try
        {
            clear = ProtectedData.Unprotect(
                legacyEncrypted,
                legacyEntropy,
                DataProtectionScope.CurrentUser);
            if (clear.Length == 0)
            {
                throw new InvalidDataException("A legacy credential is empty.");
            }

            migratedEncrypted = ProtectedData.Protect(
                clear,
                currentEntropy,
                DataProtectionScope.CurrentUser);
            await AtomicFile.WriteAllBytesAsync(
                targetPath,
                migratedEncrypted,
                cancellationToken);
            return clear;
        }
        catch
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyEncrypted);
            if (migratedEncrypted is not null)
            {
                CryptographicOperations.ZeroMemory(migratedEncrypted);
            }
        }
    }

    private static async Task CopyJsonIfPresentAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        using var _ = JsonDocument.Parse(bytes);
        await AtomicFile.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The JSON file '{Path.GetFileName(path)}' is invalid.",
                exception);
        }
    }

    private static Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        AtomicFile.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            cancellationToken);

    private static bool IsSafeCredentialFile(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
        string.Equals(Path.GetExtension(fileName), ".bin", StringComparison.OrdinalIgnoreCase);

    private static string GetMarkerPath(AppPaths paths) =>
        Path.Combine(paths.Root, MarkerFileName);

    private void PruneStaleStagingDirectoriesBestEffort()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         _targetPaths.LocalAppDataRoot,
                         $"{StagingPrefix}*",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteStagingDirectory(directory);
            }
        }
        catch
        {
            // A new unique staging directory is used even when cleanup is blocked.
        }
    }

    private void TryDeleteStagingDirectory(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            var expectedPrefix = Path.Combine(
                _targetPaths.LocalAppDataRoot,
                StagingPrefix);
            if (!resolved.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolved, _targetPaths.Root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolved, _legacyPaths.Root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
        catch
        {
            // Failed staging cleanup never authorizes touching either data root.
        }
    }

    private sealed record MigrationMarker(
        int SchemaVersion,
        string SourceDirectoryName,
        DateTimeOffset CompletedAtUtc);
}
