using GptController.Infrastructure;
using GptController.Services;

namespace GptController.Tests;

public sealed class InstallerUninstallSmokeBridgeTests
{
    private const string SmokeModeVariable =
        "GPT_CONTROLLER_INSTALLER_SMOKE_MODE";
    private const string HelperPathVariable =
        "GPT_CONTROLLER_INSTALLER_SMOKE_HELPER_PATH";

    [Fact]
    public async Task SeedActiveProviderForInstallerSmoke()
    {
        var smokeMode = Environment.GetEnvironmentVariable(SmokeModeVariable);
        if (string.IsNullOrEmpty(smokeMode))
        {
            return;
        }

        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("Windows", Environment.GetEnvironmentVariable("RUNNER_OS"));
        Assert.Equal(
            "github-hosted",
            Environment.GetEnvironmentVariable(
                "GPT_CONTROLLER_SMOKE_RUNNER_ENVIRONMENT"));
        Assert.Equal("seed-active-provider", smokeMode);

        var helperPath = Environment.GetEnvironmentVariable(HelperPathVariable);
        Assert.False(string.IsNullOrWhiteSpace(helperPath));
        helperPath = Path.GetFullPath(helperPath);
        Assert.True(File.Exists(helperPath));

        var paths = new AppPaths();
        Assert.True(File.Exists(paths.CodexConfigFile));
        Assert.False(File.Exists(paths.DeepSeekConfigStateFile));
        paths.EnsureCreated();
        var configService = new DeepSeekCodexConfigService(
            new DeepSeekCodexConfigOptions(
                paths.CodexConfigFile,
                paths.DeepSeekModelCatalogFile,
                paths.DeepSeekConfigStateFile,
                helperPath));

        var result = await configService.ApplyAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Applied, result.Status);
        Assert.True(File.Exists(result.BackupFilePath));
        Assert.True(File.Exists(paths.DeepSeekConfigStateFile));
    }
}
