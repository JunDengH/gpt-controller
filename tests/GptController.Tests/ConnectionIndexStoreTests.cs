using System.Text.Json;
using System.Text.Json.Serialization;
using GptController.Infrastructure;
using GptController.Models;

namespace GptController.Tests;

public sealed class ConnectionIndexStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task SaveProjectionAsync_MapsAndSortsChatGptConnections()
    {
        await using var harness = IndexHarness.Create();
        var active = CreateProfile(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            "active-account",
            isActive: true);
        var first = CreateProfile(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "first-account",
            isActive: false);
        var deepSeek = new DeepSeekConnection
        {
            Nickname = "DeepSeek",
            KeyLastFour = "ABCD",
            IsActive = false,
            Status = DeepSeekConnectionStatus.Available
        };

        var saved = await harness.Store.SaveProjectionAsync(
            [active, first],
            deepSeek);

        Assert.Equal(ConnectionIndex.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.Equal(
            [first.Id, active.Id],
            saved.ChatGptConnections.Select(item => item.ProfileId));
        var mapped = saved.ChatGptConnections.Single(item => item.ProfileId == active.Id);
        Assert.Equal(active.Nickname, mapped.Nickname);
        Assert.Equal(active.Email, mapped.Email);
        Assert.Equal(active.AccountId, mapped.AccountId);
        Assert.Equal(active.MembershipPlan, mapped.MembershipPlan);
        Assert.Equal(active.Ownership, mapped.Ownership);
        Assert.True(mapped.IsActive);
        Assert.Equal(deepSeek, saved.DeepSeekConnection);
        Assert.Equal(ConnectionProvider.ChatGpt, saved.ActiveConnection?.Provider);
        Assert.Equal(active.Id.ToString("N"), saved.ActiveConnection?.ConnectionId);

        var loaded = await harness.Store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(
            saved.ChatGptConnections.Select(item => item.Id),
            loaded.ChatGptConnections.Select(item => item.Id));
        Assert.Equal(saved.ActiveConnection, loaded.ActiveConnection);
        Assert.Contains(
            "\"provider\": \"chatGpt\"",
            await File.ReadAllTextAsync(harness.Paths.ConnectionIndexFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveProjectionAsync_ProjectsActiveDeepSeekConnection()
    {
        await using var harness = IndexHarness.Create();
        var profile = CreateProfile(Guid.NewGuid(), "chatgpt", isActive: false);
        var deepSeek = new DeepSeekConnection
        {
            Nickname = "DeepSeek Active",
            KeyLastFour = "WXYZ",
            IsActive = true,
            Status = DeepSeekConnectionStatus.Available
        };

        var saved = await harness.Store.SaveProjectionAsync([profile], deepSeek);

        Assert.Equal(ConnectionProvider.DeepSeek, saved.ActiveConnection?.Provider);
        Assert.Equal(DeepSeekConnection.FixedId, saved.ActiveConnection?.ConnectionId);
        Assert.True(saved.DeepSeekConnection?.IsActive);
    }

    [Fact]
    public async Task SaveProjectionAsync_RejectsMultipleActiveProviders()
    {
        await using var harness = IndexHarness.Create();
        var profile = CreateProfile(Guid.NewGuid(), "chatgpt", isActive: true);
        var deepSeek = new DeepSeekConnection
        {
            KeyLastFour = "ABCD",
            IsActive = true
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Store.SaveProjectionAsync([profile], deepSeek));
        Assert.False(File.Exists(harness.Paths.ConnectionIndexFile));
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsupportedSchema()
    {
        await using var harness = IndexHarness.Create();
        await harness.WriteIndexAsync(new ConnectionIndex
        {
            SchemaVersion = ConnectionIndex.CurrentSchemaVersion + 1
        });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RejectsInconsistentActiveReference()
    {
        await using var harness = IndexHarness.Create();
        var profile = CreateProfile(Guid.NewGuid(), "inactive", isActive: false);
        await harness.WriteIndexAsync(new ConnectionIndex
        {
            ChatGptConnections =
            [
                new ChatGptConnection
                {
                    Id = profile.Id.ToString("N"),
                    ProfileId = profile.Id,
                    Nickname = profile.Nickname,
                    Email = profile.Email,
                    AccountId = profile.AccountId,
                    IsActive = false,
                    MembershipPlan = profile.MembershipPlan,
                    Ownership = profile.Ownership,
                    UpdatedAt = profile.UpdatedAt
                }
            ],
            ActiveConnection = new ActiveConnectionRef
            {
                Provider = ConnectionProvider.ChatGpt,
                ConnectionId = profile.Id.ToString("N")
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RejectsMalformedChatGptProjection()
    {
        await using var harness = IndexHarness.Create();
        var profileId = Guid.NewGuid();
        await harness.WriteIndexAsync(new ConnectionIndex
        {
            ChatGptConnections =
            [
                new ChatGptConnection
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ProfileId = profileId,
                    Nickname = "Mismatch",
                    Email = "mismatch@example.com",
                    AccountId = "account",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Store.LoadAsync());
    }

    private static AccountProfile CreateProfile(
        Guid id,
        string accountId,
        bool isActive) => new()
        {
            Id = id,
            Nickname = $"Profile {accountId}",
            Email = $"{accountId}@example.com",
            AccountId = accountId,
            IsActive = isActive,
            MembershipPlan = MembershipPlan.Pro5x,
            Ownership = AccountOwnership.Organization("org-1", "Example Org"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z")
        };

    private sealed class IndexHarness : IAsyncDisposable
    {
        private IndexHarness(string root)
        {
            Root = root;
            Paths = new AppPaths(
                Path.Combine(root, "local"),
                Path.Combine(root, "profile"));
            Paths.EnsureCreated();
            Store = new ConnectionIndexStore(Paths);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public ConnectionIndexStore Store { get; }

        public static IndexHarness Create() => new(Path.Combine(
            Path.GetTempPath(),
            "GptController.Tests",
            Guid.NewGuid().ToString("N")));

        public async Task WriteIndexAsync(ConnectionIndex index)
        {
            Directory.CreateDirectory(Paths.Connections);
            await File.WriteAllBytesAsync(
                Paths.ConnectionIndexFile,
                JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions));
        }

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
                // Test cleanup is best effort.
            }

            return ValueTask.CompletedTask;
        }
    }
}
