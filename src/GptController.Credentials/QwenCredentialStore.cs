using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GptController.Credentials;

public sealed class QwenCredentialStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes(ApplicationDataLayout.QwenCredentialEntropyPurpose);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _connectionsDirectory;
    private readonly string _credentialsDirectory;
    private readonly string _metadataPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QwenCredentialStore(string? applicationDataRoot = null)
    {
        Root = applicationDataRoot ?? ApplicationDataLayout.GetDefaultRoot();
        _connectionsDirectory = Path.Combine(Root, "connections");
        _credentialsDirectory = Path.Combine(Root, "credentials", "qwen");
        _metadataPath = Path.Combine(_connectionsDirectory, "qwen-credential.json");
    }

    public string Root { get; }

    public async Task<QwenCredentialMetadata?> GetMetadataAsync(
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

    public async Task<QwenCredentialMetadata> SaveAsync(
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
        ValidateApiKey(apiKey);
        var normalizedKey = apiKey.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_connectionsDirectory);
            Directory.CreateDirectory(_credentialsDirectory);
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
                    "The Qwen API key could not be stored securely.",
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
            return new PreparedCredentialSave(
                this,
                new QwenCredentialMetadata
                {
                    KeyLastFour = normalizedKey[^4..],
                    CredentialFile = credentialFile,
                    CreatedAt = existing?.CreatedAt ?? now,
                    UpdatedAt = now
                },
                credentialPath,
                existing);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var metadata = await ReadMetadataCoreAsync(cancellationToken)
                ?? throw new CredentialStoreException("No Qwen API credential is configured.");
            var path = GetCredentialPath(metadata);
            byte[] protectedBytes;
            try
            {
                protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CredentialStoreException(
                    "The Qwen API credential is unavailable.",
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
                ValidateApiKey(apiKey);
                return apiKey;
            }
            catch (CredentialStoreException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is CryptographicException or ArgumentException)
            {
                throw new CredentialStoreException(
                    "The Qwen API credential could not be decrypted for this Windows user.",
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
            Exception? failure = TryDelete(_metadataPath, null);
            if (Directory.Exists(_credentialsDirectory))
            {
                string[] paths;
                try
                {
                    paths = Directory.GetFiles(_credentialsDirectory);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    paths = [];
                    failure ??= exception;
                }

                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    failure = TryDelete(path, failure);
                }
            }

            if (failure is not null || File.Exists(_metadataPath) ||
                (Directory.Exists(_credentialsDirectory) &&
                 Directory.EnumerateFiles(_credentialsDirectory).Any()))
            {
                const string message =
                    "The Qwen API credential could not be deleted completely. Retry the operation.";
                throw failure is null
                    ? new CredentialStoreException(message)
                    : new CredentialStoreException(message, failure);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<QwenCredentialMetadata?> ReadMetadataCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_metadataPath);
            var metadata = await JsonSerializer.DeserializeAsync<QwenCredentialMetadata>(
                stream,
                JsonOptions,
                cancellationToken);
            if (metadata is null ||
                metadata.SchemaVersion != QwenCredentialMetadata.CurrentSchemaVersion ||
                !string.Equals(metadata.Provider, ApplicationDataLayout.QwenProvider, StringComparison.Ordinal) ||
                metadata.KeyLastFour is not { Length: 4 } ||
                !IsSafeCredentialFile(metadata.CredentialFile))
            {
                throw new CredentialStoreException("The Qwen credential metadata is invalid.");
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
                "The Qwen credential metadata could not be read.",
                exception);
        }
    }

    private async Task CommitPreparedSaveAsync(
        PreparedCredentialSave update,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(update.Owner, this) || update.IsCompleted)
        {
            throw new InvalidOperationException("The prepared Qwen credential update is no longer active.");
        }

        try
        {
            await AtomicCredentialFile.WriteAsync(
                _metadataPath,
                JsonSerializer.SerializeToUtf8Bytes(update.Metadata, JsonOptions),
                cancellationToken);
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

            foreach (var path in Directory.EnumerateFiles(_credentialsDirectory, "*.bin"))
            {
                if (!string.Equals(
                        Path.GetFileName(path),
                        update.Metadata.CredentialFile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AtomicCredentialFile.TryDelete(path);
                }
            }
        }
        catch
        {
            // The new metadata is already durable; old encrypted generations can be pruned later.
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

    private string GetCredentialPath(QwenCredentialMetadata metadata) =>
        IsSafeCredentialFile(metadata.CredentialFile)
            ? Path.Combine(_credentialsDirectory, metadata.CredentialFile)
            : throw new CredentialStoreException("The Qwen credential metadata is invalid.");

    private static bool IsSafeCredentialFile(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
        string.Equals(Path.GetExtension(fileName), ".bin", StringComparison.OrdinalIgnoreCase);

    private static void ValidateApiKey(string apiKey)
    {
        var normalized = apiKey?.Trim() ?? string.Empty;
        if (normalized.Length < 4 ||
            !normalized.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("sk-sp-", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(character => character is < '!' or > '~'))
        {
            throw new CredentialStoreException(
                "Only a pay-as-you-go Alibaba Cloud Model Studio API key is supported.");
        }
    }

    private static Exception? TryDelete(string path, Exception? failure)
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

    public sealed class PreparedCredentialSave : IAsyncDisposable
    {
        private readonly QwenCredentialStore _owner;
        private bool _isCompleted;

        internal PreparedCredentialSave(
            QwenCredentialStore owner,
            QwenCredentialMetadata metadata,
            string credentialPath,
            QwenCredentialMetadata? previousMetadata)
        {
            _owner = owner;
            Metadata = metadata;
            CredentialPath = credentialPath;
            PreviousMetadata = previousMetadata;
        }

        public QwenCredentialMetadata Metadata { get; }
        internal QwenCredentialStore Owner => _owner;
        internal string CredentialPath { get; }
        internal QwenCredentialMetadata? PreviousMetadata { get; }
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
