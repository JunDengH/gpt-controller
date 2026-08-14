using System.Text.Json;
using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class UninstallCodexConfigRestoreRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ConnectionProvider.DeepSeek)]
    [InlineData(ConnectionProvider.Qwen)]
    public async Task ActiveManagedProviderIsRestoredBeforeUninstall(
        ConnectionProvider provider)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        var definition = provider == ConnectionProvider.DeepSeek
            ? ApiProviderDefinitions.ForDeepSeek(DeepSeekDefaults.ProModel)
            : ApiProviderDefinitions.ForQwen(new QwenConnection
            {
                Model = "qwen3-coder-plus",
                Region = QwenRegion.Virginia,
                Models = [new ApiModelDescriptor { Id = "qwen3-coder-plus" }]
            });
        await fixture.ConfigService.ApplyAsync(definition);
        var applied = await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile);
        applied += "\n[mcp_servers.added_after_provider]\ncommand = \"keep-me\"\n";
        await File.WriteAllTextAsync(fixture.Paths.CodexConfigFile, applied);

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.Success, result);
        var restored = await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile);
        Assert.Contains("model = \"gpt-original\"", restored);
        Assert.Contains("model_provider = \"openai\"", restored);
        Assert.Contains("custom_root = \"preserved\"", restored);
        Assert.Contains("[mcp_servers.added_after_provider]", restored);
        Assert.Contains("command = \"keep-me\"", restored);
        Assert.DoesNotContain(DeepSeekCodexConfigService.ProviderId, restored);
        Assert.DoesNotContain(DeepSeekCodexConfigService.QwenProviderId, restored);
        Assert.False(File.Exists(fixture.Paths.DeepSeekConfigStateFile));
    }

    [Fact]
    public async Task ManagedFieldConflictBlocksUninstallAndPreservesRequiredFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        await fixture.ConfigService.ApplyAsync();
        var conflicted = (await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile))
            .Replace(
                "model_reasoning_effort = \"high\"",
                "model_reasoning_effort = \"low\"",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.Paths.CodexConfigFile, conflicted);

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.Conflict, result);
        Assert.Equal(
            conflicted,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
        Assert.True(File.Exists(fixture.Paths.DeepSeekConfigStateFile));
        Assert.True(File.Exists(fixture.HelperPath));
    }

    [Fact]
    public async Task ActiveProviderRestorePreservesOriginalConfigExactly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        await fixture.ConfigService.ApplyAsync();

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.Success, result);
        Assert.Equal(
            fixture.OriginalConfig,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task MissingEncryptedBackupBlocksUninstallAndRetainsState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        var applied = await fixture.ConfigService.ApplyAsync();
        File.Delete(applied.BackupFilePath!);

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.Failure, result);
        Assert.True(File.Exists(fixture.Paths.DeepSeekConfigStateFile));
        Assert.True(File.Exists(fixture.HelperPath));
    }

    [Fact]
    public async Task ActiveManagedProviderWithoutStateBlocksUnsafeUninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        await fixture.ConfigService.ApplyAsync();
        File.Delete(fixture.Paths.DeepSeekConfigStateFile);
        var applied = await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile);

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.UnsafeConfiguration, result);
        Assert.Equal(
            applied,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
        Assert.True(File.Exists(fixture.HelperPath));
    }

    [Theory]
    [InlineData(DeepSeekCodexConfigService.ProviderId)]
    [InlineData(DeepSeekCodexConfigService.QwenProviderId)]
    public async Task ManagedProviderSectionWithOldHelperPathBlocksUnsafeUninstall(
        string providerId)
    {
        var fixture = await CreateFixtureAsync();
        var oldHelperPath = Path.Combine(
            _root,
            "previous-install",
            CredentialHelperLocator.ExecutableName);
        await File.AppendAllTextAsync(
            fixture.Paths.CodexConfigFile,
            $"\n[model_providers.{providerId}.auth]\n" +
            $"command = {JsonSerializer.Serialize(oldHelperPath)}\n");

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.UnsafeConfiguration, result);
    }

    [Theory]
    [InlineData(@"C:\tools\UnrelatedCredentialHelper.exe")]
    [InlineData(@"powershell.exe -File C:\old\GptController.CredentialHelper.exe")]
    public async Task UnrelatedManagedProviderCommandDoesNotBlockUninstall(
        string unrelatedCommand)
    {
        var fixture = await CreateFixtureAsync();
        await File.AppendAllTextAsync(
            fixture.Paths.CodexConfigFile,
            $"\n[model_providers.{DeepSeekCodexConfigService.ProviderId}.auth]\n" +
            $"command = {JsonSerializer.Serialize(unrelatedCommand)}\n");

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.Success, result);
    }

    [Fact]
    public async Task ConcurrentConnectionSwitchBlocksUninstallRestore()
    {
        var fixture = await CreateFixtureAsync();
        using var switchSemaphore = new Semaphore(1, 1, fixture.SwitchMutexName);
        Assert.True(switchSemaphore.WaitOne(0));

        try
        {
            var result = await UninstallCodexConfigRestoreRunner.RunAsync(
                fixture.Paths,
                fixture.HelperPath,
                fixture.SwitchMutexName);

            Assert.Equal(UninstallConfigRestoreExitCode.SwitchInProgress, result);
        }
        finally
        {
            switchSemaphore.Release();
        }
    }

    [Fact]
    public async Task UnmanagedConfigurationNeedsNoRestore()
    {
        var fixture = await CreateFixtureAsync();

        var result = await UninstallCodexConfigRestoreRunner.RunAsync(
            fixture.Paths,
            fixture.HelperPath,
            fixture.SwitchMutexName);

        Assert.Equal(UninstallConfigRestoreExitCode.Success, result);
        Assert.Equal(
            fixture.OriginalConfig,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    private async Task<ConfigFixture> CreateFixtureAsync()
    {
        var paths = new AppPaths(
            Path.Combine(_root, "local-app-data"),
            Path.Combine(_root, "user-profile"),
            "GptControllerUninstallTest");
        Directory.CreateDirectory(paths.CodexResources);
        Directory.CreateDirectory(paths.CodexHome);
        var installDirectory = Path.Combine(_root, "install");
        Directory.CreateDirectory(installDirectory);
        var helperPath = Path.Combine(
            installDirectory,
            CredentialHelperLocator.ExecutableName);
        await File.WriteAllBytesAsync(helperPath, []);
        const string originalConfig =
            "model = \"gpt-original\"\r\n" +
            "model_provider = \"openai\"\r\n" +
            "custom_root = \"preserved\"\r\n";
        await File.WriteAllTextAsync(paths.CodexConfigFile, originalConfig);
        var configService = new DeepSeekCodexConfigService(
            new DeepSeekCodexConfigOptions(
                paths.CodexConfigFile,
                paths.DeepSeekModelCatalogFile,
                paths.DeepSeekConfigStateFile,
                helperPath));
        return new(
            paths,
            helperPath,
            $"Local\\GptController.UninstallTest.{Guid.NewGuid():N}",
            originalConfig,
            configService);
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
            // Best-effort test cleanup.
        }
    }

    private sealed record ConfigFixture(
        AppPaths Paths,
        string HelperPath,
        string SwitchMutexName,
        string OriginalConfig,
        DeepSeekCodexConfigService ConfigService);
}
