using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class QwenConnectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveRoundTripPreservesRegionModelsAndEncryptedCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var credentials = new QwenCredentialStore(paths.Root);
        var store = new QwenConnectionStore(paths, credentials);
        var connection = new QwenConnection
        {
            Model = "qwen3-coder-plus",
            Region = QwenRegion.Tokyo,
            WorkspaceId = "workspace-a",
            Models =
            [
                new ApiModelDescriptor { Id = "qwen3-coder-plus" },
                new ApiModelDescriptor { Id = "qwen3.8-max" }
            ],
            Status = ApiConnectionStatus.Available
        };

        var saved = await store.SaveAsync(connection, "sk-qwen-store-123456");
        var loaded = await store.GetAsync();

        Assert.NotNull(loaded);
        Assert.Equal(QwenRegion.Tokyo, loaded.Region);
        Assert.Equal("workspace-a", loaded.WorkspaceId);
        Assert.Equal(saved.Model, loaded.Model);
        Assert.Equal(2, loaded.Models.Count);
        Assert.Equal("3456", loaded.KeyLastFour);
        Assert.Equal("sk-qwen-store-123456", await credentials.ReadAsync());
    }

    [Fact]
    public async Task RejectsCustomOrUnsafeWorkspaceHostInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var store = new QwenConnectionStore(paths, new QwenCredentialStore(paths.Root));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new QwenConnection
            {
                Model = "qwen3-coder-plus",
                Region = QwenRegion.Beijing,
                WorkspaceId = "attacker.example.com",
                Models = [new ApiModelDescriptor { Id = "qwen3-coder-plus" }]
            },
            "sk-qwen-store-123456"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
