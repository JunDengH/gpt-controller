using GptController.CredentialHelper;
using GptController.Credentials;

namespace GptController.Tests;

public sealed class QwenCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QwenCredentialIsDpapiProtectedAndIsolatedFromDeepSeek()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string qwenKey = "sk-qwen-private-123456";
        const string deepSeekKey = "sk-deepseek-private-987654";
        var qwen = new QwenCredentialStore(_root);
        var deepSeek = new DeepSeekCredentialStore(_root);

        var qwenMetadata = await qwen.SaveAsync(qwenKey);
        await deepSeek.SaveAsync(deepSeekKey);

        Assert.Equal(qwenKey, await qwen.ReadAsync());
        Assert.Equal(deepSeekKey, await deepSeek.ReadAsync());
        Assert.NotEqual(
            qwenMetadata.CredentialFile,
            (await deepSeek.GetMetadataAsync())!.CredentialFile);
        Assert.DoesNotContain(
            qwenKey,
            Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(
                _root,
                "credentials",
                "qwen",
                qwenMetadata.CredentialFile))),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialHelperReturnsQwenTokenOnlyForQwenProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string apiKey = "sk-qwen-helper-123456";
        var store = new QwenCredentialStore(_root);
        await store.SaveAsync(apiKey);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CredentialHelperRunner.RunAsync(
            ["get-token", "--provider", "qwen"],
            output,
            error,
            qwenStoreFactory: () => store);

        Assert.Equal(0, exitCode);
        Assert.Equal(apiKey + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DedicatedPlanCredentialIsNotStored()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new QwenCredentialStore(_root);

        await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.SaveAsync("sk-sp-dedicated-plan-key"));
        Assert.Null(await store.GetMetadataAsync());
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
