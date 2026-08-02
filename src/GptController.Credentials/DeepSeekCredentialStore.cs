using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GptController.Credentials;

public sealed class DeepSeekCredentialStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes(
            ApplicationDataLayout.DeepSeekCredentialEntropyPurpose);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _connectionsDirectory;
    private readonly string _credentialsDirectory;
    private readonly string _metadataPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeepSeekCredentialStore(string? applicationDataRoot = null)
    {
        Root = applicationDataRoot ?? ApplicationDataLayout.GetDefaultRoot();
        _connectionsDirectory = Path.Combine(Root, "connections");
        _credentialsDirectory = Path.Combine(Root, "credentials", "deepseek");
        _metadataPath = Path.Combine(
            _connectionsDirectory,
            "deepseek-credential.json");
    }

    public string Root { get; }

    public async Task<DeepSeekCredentialMetadata?> GetMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadMetadataCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeepSeekCredentialMetadata> SaveAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        await using var update = await PrepareSaveAsync(apiKey, cancellationToken);
        await update.CommitAsync(cancellationToken);
        return update.Metadata;
    }

    public async Task<PreparedCredentialSave> PrepareSaveAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CredentialStoreException("The DeepSeek API key cannot be empty.");
        }

        var normalizedKey = apiKey.Trim();
        if (normalizedKey.Length < 4 ||
            normalizedKey.Any(character => character is < '!' or > '~'))
        {
            throw new CredentialStoreException("The DeepSeek API key is invalid.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectories();
            var existing = await ReadMetadataCoreAsync(cancellationToken);
            var credentialFile = $"{Guid.NewGuid():N}.bin";
            var credentialPath = Path.Combine(_credentialsDirectory, credentialFile);
            var plaintext = Encoding.UTF8.GetBytes(normalizedKey);
            byte[]? protectedBytes = null;

            try
            {
                protectedBytes = ProtectedData.Protect(
                    plaintext,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                await AtomicCredentialFile.WriteAsync(
                    credentialPath,
                    protectedBytes,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is CryptographicException or IOException or UnauthorizedAccessException)
            {
                AtomicCredentialFile.TryDelete(credentialPath);
                throw new CredentialStoreException(
                    "The DeepSeek API key could not be stored securely.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (protectedBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var metadata = new DeepSeekCredentialMetadata
            {
                KeyLastFour = normalizedKey[^4..],
                CredentialFile = credentialFile,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now
            };

            return new PreparedCredentialSave(
                this,
                metadata,
                credentialPath,
                existing);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task<string> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var metadata = await ReadMetadataCoreAsync(cancellationToken)
                ?? throw new CredentialStoreException(
                    "No DeepSeek API credential is configured.");
            var credentialPath = GetCredentialPath(metadata);

            byte[] protectedBytes;
            try
            {
                protectedBytes = await File.ReadAllBytesAsync(
                    credentialPath,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CredentialStoreException(
                    "The DeepSeek API credential is unavailable.",
                    exception);
            }

            byte[]? plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                var apiKey = Encoding.UTF8.GetString(plaintext);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new CredentialStoreException(
                        "The stored DeepSeek API credential is invalid.");
                }

                return apiKey;
            }
            catch (CredentialStoreException)
            {
                throw;
            }
            catch (CryptographicException exception)
            {
                throw new CredentialStoreException(
                    "The DeepSeek API credential could not be decrypted for this Windows user.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = TryDeleteFile(_metadataPath, null);

            foreach (var path in GetCredentialFiles(ref failure))
            {
                cancellationToken.ThrowIfCancellationRequested();
                failure = TryDeleteFile(path, failure);
            }

            if (File.Exists(_metadataPath) || HasCredentialFiles(ref failure))
            {
                failure ??= new IOException(
                    "One or more DeepSeek credential files still exist after deletion.");
            }

            if (failure is not null)
            {
                throw new CredentialStoreException(
                    "The DeepSeek API credential could not be deleted completely. Retry the operation.",
                    failure);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeepSeekCredentialMetadata?> ReadMetadataCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = await JsonSerializer.DeserializeAsync<DeepSeekCredentialMetadata>(
                stream,
                JsonOptions,
                cancellationToken);

            if (metadata is null ||
                metadata.SchemaVersion != DeepSeekCredentialMetadata.CurrentSchemaVersion ||
                !string.Equals(
                    metadata.Provider,
                    ApplicationDataLayout.DeepSeekProvider,
                    StringComparison.Ordinal) ||
                metadata.KeyLastFour is not { Length: 4 } ||
                !IsSafeCredentialFile(metadata.CredentialFile))
            {
                throw new CredentialStoreException(
                    "The DeepSeek credential metadata is invalid.");
            }

            return metadata;
        }
        catch (CredentialStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new CredentialStoreException(
                "The DeepSeek credential metadata could not be read.",
                exception);
        }
    }

    private string GetCredentialPath(DeepSeekCredentialMetadata metadata)
    {
        if (!IsSafeCredentialFile(metadata.CredentialFile))
        {
            throw new CredentialStoreException(
                "The DeepSeek credential metadata is invalid.");
        }

        return Path.Combine(_credentialsDirectory, metadata.CredentialFile);
    }

    private static bool IsSafeCredentialFile(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
        string.Equals(Path.GetExtension(fileName), ".bin", StringComparison.OrdinalIgnoreCase);

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_connectionsDirectory);
        Directory.CreateDirectory(_credentialsDirectory);
    }

    private async Task CommitPreparedSaveAsync(
        PreparedCredentialSave update,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(update.Owner, this) || update.IsCompleted)
        {
            throw new InvalidOperationException(
                "The prepared DeepSeek credential update is no longer active.");
        }

        try
        {
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(
                update.Metadata,
                JsonOptions);
            await AtomicCredentialFile.WriteAsync(
                _metadataPath,
                metadataBytes,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            AbortPreparedSave(update);
            throw new CredentialStoreException(
                "The DeepSeek credential metadata could not be stored.",
                exception);
        }
        catch
        {
            AbortPreparedSave(update);
            throw;
        }

        update.MarkCompleted();
        try
        {
            if (update.PreviousMetadata is not null)
            {
                AtomicCredentialFile.TryDelete(GetCredentialPath(update.PreviousMetadata));
            }

            PruneUnreferencedCredentialsBestEffort(update.Metadata.CredentialFile);
        }
        catch
        {
            // Metadata already committed. Cleanup must never turn a successful
            // rotation into a reported failure that callers would roll back.
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AbortPreparedSave(PreparedCredentialSave update)
    {
        if (!ReferenceEquals(update.Owner, this) || update.IsCompleted)
        {
            return;
        }

        AtomicCredentialFile.TryDelete(update.CredentialPath);
        update.MarkCompleted();
        _gate.Release();
    }

    private static Exception? TryDeleteFile(string path, Exception? failure)
    {
        try
        {
            File.Delete(path);
            if (File.Exists(path))
            {
                throw new IOException("The credential file still exists after deletion.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return failure ?? exception;
        }

        return failure;
    }

    private IReadOnlyList<string> GetCredentialFiles(ref Exception? failure)
    {
        try
        {
            return Directory.Exists(_credentialsDirectory)
                ? Directory.GetFiles(
                    _credentialsDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                : [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failure ??= exception;
            return [];
        }
    }

    private bool HasCredentialFiles(ref Exception? failure)
    {
        try
        {
            return Directory.Exists(_credentialsDirectory) &&
                   Directory.EnumerateFiles(
                       _credentialsDirectory,
                       "*",
                       SearchOption.TopDirectoryOnly)
                       .Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failure ??= exception;
            return true;
        }
    }

    private void PruneUnreferencedCredentialsBestEffort(string currentCredentialFile)
    {
        if (!Directory.Exists(_credentialsDirectory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _credentialsDirectory,
                         "*.bin",
                         SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(
                        Path.GetFileName(path),
                        currentCredentialFile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AtomicCredentialFile.TryDelete(path);
                }
            }
        }
        catch
        {
            // The new metadata already points at a durable credential. Old,
            // DPAPI-encrypted generations can be pruned on a later save/delete.
        }
    }

    public sealed class PreparedCredentialSave : IAsyncDisposable
    {
        private readonly DeepSeekCredentialStore _owner;
        private bool _isCompleted;

        internal PreparedCredentialSave(
            DeepSeekCredentialStore owner,
            DeepSeekCredentialMetadata metadata,
            string credentialPath,
            DeepSeekCredentialMetadata? previousMetadata)
        {
            _owner = owner;
            Metadata = metadata;
            CredentialPath = credentialPath;
            PreviousMetadata = previousMetadata;
        }

        public DeepSeekCredentialMetadata Metadata { get; }

        internal DeepSeekCredentialStore Owner => _owner;
        internal string CredentialPath { get; }
        internal DeepSeekCredentialMetadata? PreviousMetadata { get; }
        internal bool IsCompleted => _isCompleted;

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            _owner.CommitPreparedSaveAsync(this, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _owner.AbortPreparedSave(this);
            return ValueTask.CompletedTask;
        }

        internal void MarkCompleted() => _isCompleted = true;
    }
}
