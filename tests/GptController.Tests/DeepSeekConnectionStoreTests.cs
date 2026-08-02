using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class DeepSeekConnectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"gpt-controller-connection-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoadNeverPersistsPlaintextKeyInMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        var credentialStore = new DeepSeekCredentialStore(paths.Root);
        var store = new DeepSeekConnectionStore(paths, credentialStore);
        const string key = "sk-deepseek-secret-1234567890";

        await store.SaveAsync(new DeepSeekConnection { Nickname = "DeepSeek V4" }, key);
        var loaded = await store.GetAsync();
        var metadata = await File.ReadAllTextAsync(
            Path.Combine(paths.Root, "connections", "deepseek.json"));

        Assert.NotNull(loaded);
        Assert.Equal("7890", loaded.KeyLastFour);
        Assert.DoesNotContain(key, metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActiveUpdatesConnectionState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        var store = new DeepSeekConnectionStore(
            paths,
            new DeepSeekCredentialStore(paths.Root));
        await store.SaveAsync(
            new DeepSeekConnection { Nickname = "DeepSeek V4" },
            "sk-deepseek-secret-1234567890");

        await store.SetActiveAsync(true);

        Assert.True((await store.GetAsync())!.IsActive);
    }

    [Fact]
    public async Task Save_WhenConnectionMetadataIsLocked_DoesNotRotateExistingKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        var credentialStore = new DeepSeekCredentialStore(paths.Root);
        var store = new DeepSeekConnectionStore(paths, credentialStore);
        const string originalKey = "sk-original-deepseek-secret-1111";
        const string replacementKey = "sk-replacement-deepseek-secret-2222";
        await store.SaveAsync(
            new DeepSeekConnection { Nickname = "Original" },
            originalKey);
        var originalCredential = await credentialStore.GetMetadataAsync();
        var connectionPath = Path.Combine(
            paths.Root,
            "connections",
            "deepseek.json");

        using (var locked = new FileStream(
                   connectionPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var exception = await Record.ExceptionAsync(() => store.SaveAsync(
                new DeepSeekConnection { Nickname = "Replacement" },
                replacementKey));
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"Expected a file-system write failure, got {exception?.GetType().Name ?? "no exception"}.");
        }

        var currentCredential = await credentialStore.GetMetadataAsync();
        var currentConnection = await store.GetAsync();
        Assert.Equal(originalKey, await credentialStore.ReadAsync());
        Assert.Equal(originalCredential!.CredentialFile, currentCredential!.CredentialFile);
        Assert.Equal("Original", currentConnection!.Nickname);
        Assert.Equal("1111", currentConnection.KeyLastFour);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.Root, "credentials", "deepseek"),
            "*.bin"));
    }

    [Fact]
    public async Task Save_WhenCredentialMetadataCommitFails_RestoresConnectionAndOldKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        var credentialStore = new DeepSeekCredentialStore(paths.Root);
        var store = new DeepSeekConnectionStore(paths, credentialStore);
        const string originalKey = "sk-original-metadata-secret-3333";
        await store.SaveAsync(
            new DeepSeekConnection { Nickname = "Before commit" },
            originalKey);
        var originalCredential = await credentialStore.GetMetadataAsync();
        var credentialMetadataPath = Path.Combine(
            paths.Root,
            "connections",
            "deepseek-credential.json");

        using (var locked = new FileStream(
                   credentialMetadataPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            await Assert.ThrowsAsync<CredentialStoreException>(() => store.SaveAsync(
                new DeepSeekConnection { Nickname = "After commit" },
                "sk-replacement-metadata-secret-4444"));
        }

        var currentCredential = await credentialStore.GetMetadataAsync();
        var currentConnection = await store.GetAsync();
        Assert.Equal(originalKey, await credentialStore.ReadAsync());
        Assert.Equal(originalCredential!.CredentialFile, currentCredential!.CredentialFile);
        Assert.Equal("Before commit", currentConnection!.Nickname);
        Assert.Equal("3333", currentConnection.KeyLastFour);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.Root, "credentials", "deepseek"),
            "*.bin"));
    }

    [Fact]
    public async Task Delete_WhenConnectionMetadataIsLocked_RemovesKeyAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        var credentialStore = new DeepSeekCredentialStore(paths.Root);
        var store = new DeepSeekConnectionStore(paths, credentialStore);
        await store.SaveAsync(
            new DeepSeekConnection { Nickname = "DeepSeek V4" },
            "sk-delete-connection-secret-1357");
        var connectionPath = Path.Combine(
            paths.Root,
            "connections",
            "deepseek.json");

        using (var locked = new FileStream(
                   connectionPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var exception = await Assert.ThrowsAsync<IOException>(
                () => store.DeleteAsync());

            Assert.Contains("Retry", exception.Message, StringComparison.Ordinal);
            Assert.Null(await credentialStore.GetMetadataAsync());
            await Assert.ThrowsAsync<CredentialStoreException>(
                () => credentialStore.ReadAsync());
            Assert.NotNull(await store.GetAsync());
        }

        await store.DeleteAsync();

        Assert.Null(await store.GetAsync());
        Assert.False(File.Exists(connectionPath));
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
            // Best effort test cleanup.
        }
    }
}
