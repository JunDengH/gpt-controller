using System.Text.Json;
using GptAccountManager.CredentialHelper;
using GptAccountManager.Credentials;

namespace GptAccountManager.Tests;

public sealed class DeepSeekCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptAccountManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndRead_RoundTripsWithDpapi_WithoutPlaintextMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string apiKey = "sk-test-super-secret-1234";
        var store = new DeepSeekCredentialStore(_root);

        var metadata = await store.SaveAsync(apiKey);
        var restored = await store.ReadAsync();

        Assert.Equal(apiKey, restored);
        Assert.Equal("1234", metadata.KeyLastFour);
        Assert.Equal(ApplicationDataLayout.DeepSeekProvider, metadata.Provider);
        Assert.Equal(ApplicationDataLayout.DeepSeekModel, metadata.Model);

        var metadataText = await File.ReadAllTextAsync(
            Path.Combine(_root, "connections", "deepseek-credential.json"));
        Assert.DoesNotContain(apiKey, metadataText, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", metadataText, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(metadataText);
        Assert.Equal(
            "1234",
            document.RootElement.GetProperty("keyLastFour").GetString());

        var protectedBytes = await File.ReadAllBytesAsync(
            Path.Combine(
                _root,
                "credentials",
                "deepseek",
                metadata.CredentialFile));
        Assert.DoesNotContain(
            apiKey,
            System.Text.Encoding.UTF8.GetString(protectedBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_UpdatesCredentialAndPrunesOldGeneration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DeepSeekCredentialStore(_root);
        var first = await store.SaveAsync("sk-first-secret-1111");

        var second = await store.SaveAsync("sk-second-secret-2222");

        Assert.Equal("sk-second-secret-2222", await store.ReadAsync());
        Assert.Equal("2222", second.KeyLastFour);
        Assert.False(File.Exists(Path.Combine(
            _root,
            "credentials",
            "deepseek",
            first.CredentialFile)));
        Assert.True(File.Exists(Path.Combine(
            _root,
            "credentials",
            "deepseek",
            second.CredentialFile)));
    }

    [Fact]
    public async Task Delete_RemovesMetadataAndCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DeepSeekCredentialStore(_root);
        await store.SaveAsync("sk-delete-secret-9876");

        await store.DeleteAsync();

        Assert.Null(await store.GetMetadataAsync());
        await Assert.ThrowsAsync<CredentialStoreException>(() => store.ReadAsync());
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(_root, "credentials", "deepseek"),
            "*.bin"));
    }

    [Fact]
    public async Task Delete_WhenCredentialFileIsLocked_ReportsFailureAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DeepSeekCredentialStore(_root);
        var metadata = await store.SaveAsync("sk-delete-locked-secret-2468");
        var credentialPath = Path.Combine(
            _root,
            "credentials",
            "deepseek",
            metadata.CredentialFile);

        using (var locked = new FileStream(
                   credentialPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var exception = await Assert.ThrowsAsync<CredentialStoreException>(
                () => store.DeleteAsync());

            Assert.Contains("Retry", exception.Message, StringComparison.Ordinal);
            Assert.Null(await store.GetMetadataAsync());
            Assert.True(File.Exists(credentialPath));
            await Assert.ThrowsAsync<CredentialStoreException>(() => store.ReadAsync());
        }

        await store.DeleteAsync();

        Assert.False(File.Exists(credentialPath));
        Assert.Null(await store.GetMetadataAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("sk-valid-prefix\r\ninjected")]
    [InlineData("sk-valid-prefix\tinjected")]
    public async Task Save_RejectsInvalidKeyWithoutEchoingIt(string apiKey)
    {
        var store = new DeepSeekCredentialStore(_root);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(
            () => store.SaveAsync(apiKey));

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.DoesNotContain(apiKey.Trim(), exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Helper_SuccessWritesOnlyTokenToStandardOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string apiKey = "sk-helper-secret-1357";
        var store = new DeepSeekCredentialStore(_root);
        await store.SaveAsync(apiKey);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CredentialHelperRunner.RunAsync(
            ["get-token", "--provider", "deepseek"],
            stdout,
            stderr,
            () => store);

        Assert.Equal(0, exitCode);
        Assert.Equal(apiKey + Environment.NewLine, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task Helper_MissingCredentialWritesOnlySafeError()
    {
        var store = new DeepSeekCredentialStore(_root);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CredentialHelperRunner.RunAsync(
            ["get-token", "--provider", "deepseek"],
            stdout,
            stderr,
            () => store);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(
            "The DeepSeek API credential is unavailable." + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public async Task Helper_InvalidArgumentsReturnsUsageWithoutReadingStore()
    {
        var factoryCalled = false;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CredentialHelperRunner.RunAsync(
            ["get-token", "--provider", "other"],
            stdout,
            stderr,
            () =>
            {
                factoryCalled = true;
                return new DeepSeekCredentialStore(_root);
            });

        Assert.Equal(2, exitCode);
        Assert.False(factoryCalled);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(
            "Usage: get-token --provider deepseek" + Environment.NewLine,
            stderr.ToString());
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
            // Test cleanup only.
        }
    }
}
