using System.Text.Json;
using System.Text.Json.Serialization;
using GptController.Models;

namespace GptController.Infrastructure;

public sealed class ConnectionIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionIndexStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<ConnectionIndex?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_paths.ConnectionIndexFile))
            {
                return null;
            }

            ConnectionIndex? index;
            await using (var stream = File.OpenRead(_paths.ConnectionIndexFile))
            {
                index = await JsonSerializer.DeserializeAsync<ConnectionIndex>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }
            if (index?.SchemaVersion == 1)
            {
                index = MigrateV1(index);
                await AtomicFile.WriteAllBytesAsync(
                    _paths.ConnectionIndexFile,
                    JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions),
                    cancellationToken);
            }
            Validate(index);
            return index;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The connection index is invalid JSON.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConnectionIndex> SaveProjectionAsync(
        IReadOnlyCollection<AccountProfile> profiles,
        DeepSeekConnection? deepSeek,
        CancellationToken cancellationToken = default) =>
        await SaveProjectionAsync(profiles, deepSeek, null, cancellationToken);

    public async Task<ConnectionIndex> SaveProjectionAsync(
        IReadOnlyCollection<AccountProfile> profiles,
        DeepSeekConnection? deepSeek,
        QwenConnection? qwen,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var chatGpt = profiles
                .Select(profile => new ChatGptConnection
                {
                    Id = profile.Id.ToString("N"),
                    ProfileId = profile.Id,
                    Nickname = profile.Nickname,
                    Email = profile.Email,
                    AccountId = profile.AccountId,
                    IsActive = profile.IsActive,
                    MembershipPlan = profile.MembershipPlan,
                    Ownership = profile.Ownership,
                    UpdatedAt = profile.UpdatedAt
                })
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var activeChatGpt = chatGpt.Where(item => item.IsActive).ToArray();
            var apiConnections = BuildApiConnections(deepSeek, qwen);
            var activeApi = apiConnections.Where(item => item.IsActive).ToArray();
            var activeCount = activeChatGpt.Length + activeApi.Length;
            if (activeCount > 1)
            {
                throw new InvalidDataException(
                    "More than one provider is marked active in the connection data.");
            }

            var active = activeApi.SingleOrDefault() is { } activeProvider
                ? new ActiveConnectionRef
                {
                    Provider = activeProvider.Provider,
                    ConnectionId = activeProvider.Id
                }
                : activeChatGpt.SingleOrDefault() is { } chatGptActive
                    ? new ActiveConnectionRef
                    {
                        Provider = ConnectionProvider.ChatGpt,
                        ConnectionId = chatGptActive.Id
                    }
                    : null;
            var index = new ConnectionIndex
            {
                ChatGptConnections = chatGpt,
                ApiConnections = apiConnections,
                DeepSeekConnection = deepSeek,
                ActiveConnection = active,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Validate(index);
            await AtomicFile.WriteAllBytesAsync(
                _paths.ConnectionIndexFile,
                JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions),
                cancellationToken);
            return index;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(ConnectionIndex? index)
    {
        if (index is null || index.SchemaVersion != ConnectionIndex.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The connection index schema is unsupported.");
        }

        if (index.ChatGptConnections.Any(item =>
                item.ProfileId == Guid.Empty ||
                !string.Equals(item.Id, item.ProfileId.ToString("N"), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.AccountId)) ||
            index.ChatGptConnections.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
            index.ChatGptConnections.Count)
        {
            throw new InvalidDataException("The ChatGPT connection index is invalid.");
        }

        if (index.DeepSeekConnection is { } deepSeek &&
            (!string.Equals(deepSeek.Id, DeepSeekConnection.FixedId, StringComparison.Ordinal) ||
             !DeepSeekDefaults.IsSupportedModel(deepSeek.Model)))
        {
            throw new InvalidDataException("The DeepSeek connection index is invalid.");
        }

        if (index.ApiConnections.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Model) ||
                item.Provider is not (ConnectionProvider.DeepSeek or ConnectionProvider.Qwen) ||
                (item.Provider == ConnectionProvider.DeepSeek &&
                 (!string.Equals(item.Id, DeepSeekConnection.FixedId, StringComparison.Ordinal) ||
                  !DeepSeekDefaults.IsSupportedModel(item.Model))) ||
                (item.Provider == ConnectionProvider.Qwen &&
                 (!string.Equals(item.Id, QwenConnection.FixedId, StringComparison.Ordinal) ||
                  !item.Model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)))) ||
            index.ApiConnections.Select(item => item.Provider).Distinct().Count() !=
            index.ApiConnections.Count)
        {
            throw new InvalidDataException("The API connection index is invalid.");
        }

        var activeChatGpt = index.ChatGptConnections.Where(item => item.IsActive).ToArray();
        var activeApi = index.ApiConnections.Where(item => item.IsActive).ToArray();
        var activeCount = activeChatGpt.Length + activeApi.Length;
        if (activeCount > 1)
        {
            throw new InvalidDataException("The connection index has multiple active providers.");
        }

        var expected = activeApi.SingleOrDefault() is { } activeProvider
            ? new ActiveConnectionRef
            {
                Provider = activeProvider.Provider,
                ConnectionId = activeProvider.Id
            }
            : activeChatGpt.SingleOrDefault() is { } active
                ? new ActiveConnectionRef
                {
                    Provider = ConnectionProvider.ChatGpt,
                    ConnectionId = active.Id
                }
                : null;
        if (!Equals(index.ActiveConnection, expected))
        {
            throw new InvalidDataException("The active connection reference is inconsistent.");
        }
    }

    private static IReadOnlyList<ApiConnection> BuildApiConnections(
        DeepSeekConnection? deepSeek,
        QwenConnection? qwen)
    {
        var connections = new List<ApiConnection>(2);
        if (deepSeek is not null)
        {
            connections.Add(new ApiConnection
            {
                Id = deepSeek.Id,
                Provider = ConnectionProvider.DeepSeek,
                Model = deepSeek.Model,
                IsActive = deepSeek.IsActive,
                UpdatedAt = deepSeek.UpdatedAt
            });
        }

        if (qwen is not null)
        {
            connections.Add(new ApiConnection
            {
                Id = qwen.Id,
                Provider = ConnectionProvider.Qwen,
                Model = qwen.Model,
                IsActive = qwen.IsActive,
                Region = qwen.Region.ToString(),
                WorkspaceId = qwen.WorkspaceId,
                UpdatedAt = qwen.UpdatedAt
            });
        }

        return connections.OrderBy(item => item.Provider).ToArray();
    }

    private static ConnectionIndex MigrateV1(ConnectionIndex legacy)
    {
        var apiConnections = BuildApiConnections(legacy.DeepSeekConnection, null);
        return legacy with
        {
            SchemaVersion = ConnectionIndex.CurrentSchemaVersion,
            ApiConnections = apiConnections,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
