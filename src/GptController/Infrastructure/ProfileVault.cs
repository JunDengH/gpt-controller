using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GptController.Models;

namespace GptController.Infrastructure;

public sealed class ProfileVault
{
    internal const string CredentialEntropyPurpose =
        "GptController/ProfileVault/v2";
    internal const string LegacyCredentialEntropyPurpose =
        "GptAccountManager/ProfileVault/v1";

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes(CredentialEntropyPurpose);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileVault(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<AccountProfile>> LoadProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadProfilesCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountProfile?> GetProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profiles = await LoadProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(profile => profile.Id == profileId);
    }

    public async Task<AccountProfile?> FindByAccountIdAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var profiles = await LoadProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(
            profile => string.Equals(
                profile.AccountId,
                accountId,
                StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AccountProfile?> GetActiveProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = await LoadProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(profile => profile.IsActive);
    }

    public async Task<AccountProfile> UpsertProfileAsync(
        AccountProfile profile,
        ReadOnlyMemory<byte>? credential = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadProfilesCoreAsync(cancellationToken)).ToList();
            var existingIndex = profiles.FindIndex(item =>
                item.Id == profile.Id ||
                string.Equals(item.AccountId, profile.AccountId, StringComparison.OrdinalIgnoreCase));

            AccountProfile stored;
            if (existingIndex >= 0)
            {
                var existing = profiles[existingIndex];
                stored = profile with
                {
                    Id = existing.Id,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                profiles[existingIndex] = stored;
            }
            else
            {
                stored = profile with
                {
                    CreatedAt = profile.CreatedAt == default
                        ? DateTimeOffset.UtcNow
                        : profile.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                profiles.Add(stored);
            }

            if (stored.IsActive)
            {
                for (var index = 0; index < profiles.Count; index++)
                {
                    if (profiles[index].Id != stored.Id && profiles[index].IsActive)
                    {
                        profiles[index] = profiles[index] with
                        {
                            IsActive = false,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                    }
                }
            }

            if (credential is { } credentialValue)
            {
                await WriteCredentialCoreAsync(stored.Id, credentialValue, cancellationToken);
            }

            await SaveProfilesCoreAsync(profiles, cancellationToken);
            return stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveProfilesAsync(
        IReadOnlyCollection<AccountProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveProfilesCoreAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActiveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadProfilesCoreAsync(cancellationToken))
                .Select(profile => profile with
                {
                    IsActive = profile.Id == profileId,
                    UpdatedAt = profile.Id == profileId || profile.IsActive
                        ? DateTimeOffset.UtcNow
                        : profile.UpdatedAt
                })
                .ToList();

            if (profiles.All(profile => profile.Id != profileId))
            {
                throw new InvalidOperationException("The selected account profile does not exist.");
            }

            await SaveProfilesCoreAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearActiveProfileAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            var profiles = (await LoadProfilesCoreAsync(cancellationToken))
                .Select(profile =>
                {
                    if (!profile.IsActive)
                    {
                        return profile;
                    }

                    changed = true;
                    return profile with
                    {
                        IsActive = false,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                })
                .ToList();
            if (changed)
            {
                await SaveProfilesCoreAsync(profiles, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadCredentialAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var encryptedPath = _paths.GetCredentialPath(profileId);
        var encrypted = await File.ReadAllBytesAsync(encryptedPath, cancellationToken);
        return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
    }

    public async Task WriteCredentialAsync(
        Guid profileId,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteCredentialCoreAsync(profileId, credential, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CreateBackupAsync(
        ReadOnlyMemory<byte> credential,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var encrypted = ProtectedData.Protect(
                credential.ToArray(),
                Entropy,
                DataProtectionScope.CurrentUser);
            var backupName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{profileId:N}.bin";
            var backupPath = Path.Combine(_paths.Backups, backupName);
            await AtomicFile.WriteAllBytesAsync(backupPath, encrypted, cancellationToken);
            PruneBackupsCore(10);
            return backupName;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadBackupAsync(
        string backupName,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetFileName(backupName), backupName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid backup name.");
        }

        var backupPath = Path.Combine(_paths.Backups, backupName);
        var encrypted = await File.ReadAllBytesAsync(backupPath, cancellationToken);
        return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
    }

    public async Task DeleteProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadProfilesCoreAsync(cancellationToken))
                .Where(profile => profile.Id != profileId)
                .ToList();
            await SaveProfilesCoreAsync(profiles, cancellationToken);
            AtomicFile.TryDelete(_paths.GetCredentialPath(profileId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AccountProfile>> LoadProfilesCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.IndexFile))
        {
            return [];
        }

        await using var stream = new FileStream(
            _paths.IndexFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<List<AccountProfile>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveProfilesCoreAsync(
        IReadOnlyCollection<AccountProfile> profiles,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profiles, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(_paths.IndexFile, bytes, cancellationToken);
    }

    private async Task WriteCredentialCoreAsync(
        Guid profileId,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken)
    {
        if (credential.IsEmpty)
        {
            throw new InvalidOperationException("Credential data cannot be empty.");
        }

        var encrypted = ProtectedData.Protect(
            credential.ToArray(),
            Entropy,
            DataProtectionScope.CurrentUser);
        await AtomicFile.WriteAllBytesAsync(
            _paths.GetCredentialPath(profileId),
            encrypted,
            cancellationToken);
    }

    private void PruneBackupsCore(int keep)
    {
        foreach (var file in new DirectoryInfo(_paths.Backups)
                     .EnumerateFiles("*.bin")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(keep))
        {
            AtomicFile.TryDelete(file.FullName);
        }
    }
}
