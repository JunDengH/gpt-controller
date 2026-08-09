using System.Text.Json;
using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;

namespace GptController.Services;

public sealed class QwenConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _connectionPath;
    private readonly QwenCredentialStore _credentialStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QwenConnectionStore(AppPaths paths, QwenCredentialStore credentialStore)
    {
        _connectionPath = Path.Combine(paths.Connections, "qwen.json");
        _credentialStore = credentialStore;
    }

    public async Task<QwenConnection?> GetAsync(CancellationToken cancellationToken = default)
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

    public async Task<QwenConnection> SaveAsync(
        QwenConnection connection,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Validate(connection);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadCoreAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await using var credentialUpdate =
                    await _credentialStore.PrepareSaveAsync(apiKey, cancellationToken);
                var stored = CreateStored(connection, existing, credentialUpdate.Metadata);
                var snapshot = await ReadSnapshotAsync(cancellationToken);
                try
                {
                    await WriteCoreAsync(stored, cancellationToken);
                    await credentialUpdate.CommitAsync(cancellationToken);
                    return stored;
                }
                catch (Exception exception)
                {
                    try
                    {
                        await RestoreSnapshotAsync(snapshot, CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new IOException(
                            "The Qwen connection update failed and its metadata could not be restored.",
                            new AggregateException(exception, rollbackException));
                    }

                    throw;
                }
            }

            var credential = await _credentialStore.GetMetadataAsync(cancellationToken)
                ?? throw new InvalidOperationException("千问 API Key 尚未配置。");
            var result = CreateStored(connection, existing, credential);
            await WriteCoreAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActiveAsync(bool isActive, CancellationToken cancellationToken = default)
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
                connection with { IsActive = isActive, UpdatedAt = DateTimeOffset.UtcNow },
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
            File.Delete(_connectionPath);
            if (File.Exists(_connectionPath))
            {
                throw new IOException("千问连接元数据未能删除，请重试。");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<QwenConnection?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_connectionPath);
        var connection = await JsonSerializer.DeserializeAsync<QwenConnection>(
            stream,
            JsonOptions,
            cancellationToken);
        Validate(connection);
        var credential = await _credentialStore.GetMetadataAsync(cancellationToken);
        return credential is null
            ? connection! with
            {
                KeyLastFour = string.Empty,
                Status = ApiConnectionStatus.AuthenticationRequired,
                ErrorCode = "credential_missing"
            }
            : connection! with { KeyLastFour = credential.KeyLastFour };
    }

    private Task WriteCoreAsync(QwenConnection connection, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(
            _connectionPath,
            JsonSerializer.Serialize(connection, JsonOptions) + Environment.NewLine,
            cancellationToken);

    private static QwenConnection CreateStored(
        QwenConnection connection,
        QwenConnection? existing,
        QwenCredentialMetadata credential)
    {
        var now = DateTimeOffset.UtcNow;
        return connection with
        {
            Id = QwenConnection.FixedId,
            WorkspaceId = connection.WorkspaceId?.Trim(),
            KeyLastFour = credential.KeyLastFour,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Models = connection.Models
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void Validate(QwenConnection? connection)
    {
        if (connection is null ||
            !string.Equals(connection.Id, QwenConnection.FixedId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(connection.Model) ||
            !connection.Model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ||
            !Enum.IsDefined(connection.Region))
        {
            throw new InvalidDataException("千问连接元数据无效。");
        }

        _ = QwenRegions.Get(connection.Region).CreateBaseUrl(connection.WorkspaceId);
        if (connection.Models.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                !item.Id.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("千问模型缓存无效。");
        }
    }

    private async Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        File.Exists(_connectionPath)
            ? new(true, await File.ReadAllBytesAsync(_connectionPath, cancellationToken))
            : new(false, []);

    private async Task RestoreSnapshotAsync(
        ConnectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Existed)
        {
            File.Delete(_connectionPath);
            return;
        }

        await AtomicFile.WriteAllBytesAsync(_connectionPath, snapshot.Content, cancellationToken);
    }

    private sealed record ConnectionSnapshot(bool Existed, byte[] Content);
}
