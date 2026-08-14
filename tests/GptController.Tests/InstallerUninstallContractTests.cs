using GptController.Services;

namespace GptController.Tests;

public sealed class InstallerUninstallContractTests
{
    [Fact]
    public void InstallerRestoresConfigBeforeDeletingApplicationFiles()
    {
        var script = ReadInstallerScript();

        Assert.Contains(
            "AppMutex=Local\\GptAccountManager.Application",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if CurUninstallStep <> usUninstall then",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationPath := ExpandConstant('{app}\\{#MyAppExeName}')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            UninstallCodexConfigRestoreRunner.CommandLineArgument,
            script,
            StringComparison.Ordinal);
        Assert.Contains("ewWaitUntilTerminated", script, StringComparison.Ordinal);
        Assert.Contains(
            $"RestoreExitSwitchInProgress = {(int)UninstallConfigRestoreExitCode.SwitchInProgress}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"RestoreExitConflict = {(int)UninstallConfigRestoreExitCode.Conflict}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"RestoreExitUnsafeConfiguration = {(int)UninstallConfigRestoreExitCode.UnsafeConfiguration}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"RestoreExitFailure = {(int)UninstallConfigRestoreExitCode.Failure}",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceModeRunsBeforeSingleInstanceAndUiInitialization()
    {
        var appSource = ReadRepositoryFile(
            "src",
            "GptController",
            "App.xaml.cs");
        var maintenanceMode = appSource.IndexOf(
            "UninstallCodexConfigRestoreRunner.IsRequested(e.Args)",
            StringComparison.Ordinal);
        var singleInstance = appSource.IndexOf(
            "new Mutex(",
            StringComparison.Ordinal);
        var uiInitialization = appSource.IndexOf(
            "new MainWindowViewModel(",
            StringComparison.Ordinal);

        Assert.True(maintenanceMode >= 0);
        Assert.True(singleInstance > maintenanceMode);
        Assert.True(uiInitialization > maintenanceMode);
    }

    [Fact]
    public void AnyRestoreFailureAbortsUninstallIncludingSilentMode()
    {
        var script = ReadInstallerScript();
        var failureHandler = SliceProcedure(
            script,
            "procedure AbortUninstallForRestoreFailure",
            "procedure CurUninstallStepChanged");
        var uninstallHandler = script[script.IndexOf(
            "procedure CurUninstallStepChanged",
            StringComparison.Ordinal)..];

        Assert.Contains("Log(FullMessage)", failureHandler, StringComparison.Ordinal);
        Assert.Contains(
            "if not UninstallSilent then",
            failureHandler,
            StringComparison.Ordinal);
        Assert.Contains("Abort;", failureHandler, StringComparison.Ordinal);
        Assert.Contains(
            "if ResultCode <> 0 then",
            uninstallHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "AbortUninstallForRestoreFailure(RestoreFailureMessage(ResultCode))",
            uninstallHandler,
            StringComparison.Ordinal);
    }

    private static string SliceProcedure(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing installer marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing installer marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadInstallerScript() =>
        ReadRepositoryFile("installer", "GptController.iss");

    private static string ReadRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = segments.Aggregate(
                directory.FullName,
                Path.Combine);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(segments)}.");
    }
}
