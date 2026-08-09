using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class QwenConnectionSwitchCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeepSeekAndQwenSwitchAtomicallyWithoutMultipleActiveProviders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.CodexHome);
        await File.WriteAllTextAsync(paths.CodexConfigFile, "model = \"gpt-original\"\n");
        var helper = Path.Combine(_root, "helper.exe");
        await File.WriteAllBytesAsync(helper, []);
        var deepCredentials = new DeepSeekCredentialStore(paths.Root);
        var deepStore = new DeepSeekConnectionStore(paths, deepCredentials);
        var deep = await deepStore.SaveAsync(
            new DeepSeekConnection { IsActive = true },
            "sk-deepseek-switch-123456");
        var qwenCredentials = new QwenCredentialStore(paths.Root);
        var qwenStore = new QwenConnectionStore(paths, qwenCredentials);
        await qwenStore.SaveAsync(
            new QwenConnection
            {
                Model = "qwen3-coder-plus",
                Region = QwenRegion.Virginia,
                Models = [new ApiModelDescriptor { Id = "qwen3-coder-plus" }],
                Status = ApiConnectionStatus.Available
            },
            "sk-qwen-switch-123456");
        var config = new DeepSeekCodexConfigService(new(
            paths.CodexConfigFile,
            paths.DeepSeekModelCatalogFile,
            paths.DeepSeekConfigStateFile,
            helper));
        Assert.Equal(
            DeepSeekConfigChangeStatus.Applied,
            (await config.ApplyAsync(ApiProviderDefinitions.ForDeepSeek(deep.Model))).Status);
        var process = new FakeProcessController();
        var gate = new OperationGate();
        var vault = new ProfileVault(paths);
        var logger = new RedactingLogger(paths);
        var accountSwitch = new SwitchCoordinator(paths, vault, process, gate, logger);
        var coordinator = new ConnectionSwitchCoordinator(
            vault,
            deepStore,
            deepCredentials,
            config,
            accountSwitch,
            process,
            gate,
            logger,
            $@"Local\GptController.Tests.{Guid.NewGuid():N}",
            qwenStore,
            qwenCredentials);

        var toQwen = await coordinator.SwitchToQwenAsync();

        Assert.True(toQwen.IsSuccess);
        Assert.False((await deepStore.GetAsync())!.IsActive);
        Assert.True((await qwenStore.GetAsync())!.IsActive);
        Assert.Contains(
            "model_provider = \"gpt_controller_qwen\"",
            await File.ReadAllTextAsync(paths.CodexConfigFile));

        var toDeepSeek = await coordinator.SwitchToDeepSeekAsync();

        Assert.True(toDeepSeek.IsSuccess);
        Assert.True((await deepStore.GetAsync())!.IsActive);
        Assert.False((await qwenStore.GetAsync())!.IsActive);
        Assert.Contains(
            "model_provider = \"gpt_controller_deepseek\"",
            await File.ReadAllTextAsync(paths.CodexConfigFile));
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

    private sealed class FakeProcessController : IChatGptProcessController
    {
        public bool IsRunning { get; private set; } = true;
        public bool IsChatGptRunning() => IsRunning;
        public Task<bool> StopChatGptAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> LaunchChatGptAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(true);
        }
    }
}
