using System.Text.Json;
using GptAccountManager.Credentials;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed class DeepSeekConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _connectionPath;
    private readonly DeepSeekCredentialStore _credentialStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeepSeekConnectionStore(
        AppPaths paths,
        DeepSeekCredentialStore credentialStore)
    {
        _connectionPath = Path.Combine(paths.Root, "connections", "deepseek.json");
        _credentialStore = credentialStore;
    }

    public async Task<DeepSeekConnection?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeepSeekConnection> SaveAsync(
        DeepSeekConnection connection,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadCoreAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await using var credentialUpdate =
                    await _credentialStore.PrepareSaveAsync(apiKey, cancellationToken);
                var storedWithNewCredential = CreateStoredConnection(
                    connection,
                    existing,
                    credentialUpdate.Metadata);
                var snapshot = await ReadConnectionSnapshotAsync(cancellationToken);

                try
                {
                    await WriteCoreAsync(storedWithNewCredential, cancellationToken);
                    await credentialUpdate.CommitAsync(cancellationToken);
                    return storedWithNewCredential;
                }
                catch (Exception exception)
                {
                    try
                    {
                        await RestoreConnectionSnapshotAsync(
                            snapshot,
                            CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new IOException(
                            "The DeepSeek connection update failed and its metadata could not be restored.",
                            new AggregateException(exception, rollbackException));
                    }

                    throw;
                }
            }

            var credential = await _credentialStore.GetMetadataAsync(cancellationToken);

            if (credential is null)
            {
                throw new InvalidOperationException("DeepSeek API Key 尚未配置。");
            }

            var stored = CreateStoredConnection(connection, existing, credential);
            await WriteCoreAsync(stored, cancellationToken);
            return stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActiveAsync(
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await ReadCoreAsync(cancellationToken);
            if (connection is null || connection.IsActive == isActive)
            {
                return;
            }

            await WriteCoreAsync(
                connection with
                {
                    IsActive = isActive,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
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
            await _credentialStore.DeleteAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(_connectionPath);
                if (File.Exists(_connectionPath))
                {
                    throw new IOException(
                        "The DeepSeek connection metadata still exists after deletion.");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "The DeepSeek connection metadata could not be deleted. Retry the operation.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeepSeekConnection?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            _connectionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var connection = await JsonSerializer.DeserializeAsync<DeepSeekConnection>(
            stream,
            JsonOptions,
            cancellationToken);
        if (connection is null ||
            !string.Equals(connection.Id, DeepSeekConnection.FixedId, StringComparison.Ordinal) ||
            !string.Equals(connection.Model, DeepSeekDefaults.Model, StringComparison.Ordinal))
        {
            throw new InvalidDataException("DeepSeek 连接元数据无效。");
        }

        var credential = await _credentialStore.GetMetadataAsync(cancellationToken);
        return credential is null
            ? connection with
            {
                KeyLastFour = string.Empty,
                Status = DeepSeekConnectionStatus.AuthenticationRequired,
                ErrorCode = "credential_missing"
            }
            : connection with { KeyLastFour = credential.KeyLastFour };
    }

    private Task WriteCoreAsync(
        DeepSeekConnection connection,
        CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(
            _connectionPath,
            JsonSerializer.Serialize(connection, JsonOptions) + Environment.NewLine,
            cancellationToken);

    private static DeepSeekConnection CreateStoredConnection(
        DeepSeekConnection connection,
        DeepSeekConnection? existing,
        DeepSeekCredentialMetadata credential)
    {
        var now = DateTimeOffset.UtcNow;
        return connection with
        {
            Id = DeepSeekConnection.FixedId,
            Model = DeepSeekDefaults.Model,
            KeyLastFour = credential.KeyLastFour,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
    }

    private async Task<ConnectionSnapshot> ReadConnectionSnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionPath))
        {
            return new ConnectionSnapshot(false, []);
        }

        return new ConnectionSnapshot(
            true,
            await File.ReadAllBytesAsync(_connectionPath, cancellationToken));
    }

    private async Task RestoreConnectionSnapshotAsync(
        ConnectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Existed)
        {
            File.Delete(_connectionPath);
            if (File.Exists(_connectionPath))
            {
                throw new IOException(
                    "The newly-created DeepSeek connection metadata could not be removed.");
            }

            return;
        }

        if (File.Exists(_connectionPath))
        {
            var current = await File.ReadAllBytesAsync(
                _connectionPath,
                cancellationToken);
            if (current.AsSpan().SequenceEqual(snapshot.Content))
            {
                return;
            }
        }

        await AtomicFile.WriteAllBytesAsync(
            _connectionPath,
            snapshot.Content,
            cancellationToken);
    }

    private sealed record ConnectionSnapshot(bool Existed, byte[] Content);
}
