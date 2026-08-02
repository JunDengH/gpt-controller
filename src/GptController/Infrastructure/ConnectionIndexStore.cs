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

            await using var stream = File.OpenRead(_paths.ConnectionIndexFile);
            var index = await JsonSerializer.DeserializeAsync<ConnectionIndex>(
                stream,
                JsonOptions,
                cancellationToken);
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
            var activeCount = activeChatGpt.Length + (deepSeek?.IsActive == true ? 1 : 0);
            if (activeCount > 1)
            {
                throw new InvalidDataException(
                    "More than one provider is marked active in the connection data.");
            }

            var active = deepSeek?.IsActive == true
                ? new ActiveConnectionRef
                {
                    Provider = ConnectionProvider.DeepSeek,
                    ConnectionId = DeepSeekConnection.FixedId
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
             !string.Equals(deepSeek.Model, DeepSeekDefaults.Model, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The DeepSeek connection index is invalid.");
        }

        var activeChatGpt = index.ChatGptConnections.Where(item => item.IsActive).ToArray();
        var activeCount = activeChatGpt.Length +
                          (index.DeepSeekConnection?.IsActive == true ? 1 : 0);
        if (activeCount > 1)
        {
            throw new InvalidDataException("The connection index has multiple active providers.");
        }

        var expected = index.DeepSeekConnection?.IsActive == true
            ? new ActiveConnectionRef
            {
                Provider = ConnectionProvider.DeepSeek,
                ConnectionId = DeepSeekConnection.FixedId
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
}
