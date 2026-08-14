using GptController.Infrastructure;

namespace GptController.Services;

internal enum UninstallConfigRestoreExitCode
{
    Success = 0,
    SwitchInProgress = 20,
    Conflict = 21,
    UnsafeConfiguration = 22,
    Failure = 23
}

internal static class UninstallCodexConfigRestoreRunner
{
    internal const string CommandLineArgument =
        "--restore-codex-config-for-uninstall";

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 &&
        string.Equals(
            arguments[0],
            CommandLineArgument,
            StringComparison.OrdinalIgnoreCase);

    internal static async Task<UninstallConfigRestoreExitCode> RunDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var paths = new AppPaths();
            var credentialHelperPath = Path.Combine(
                AppContext.BaseDirectory,
                CredentialHelperLocator.ExecutableName);
            return await RunAsync(
                paths,
                credentialHelperPath,
                LegacyCompatibility.SwitchMutexName,
                cancellationToken);
        }
        catch
        {
            return UninstallConfigRestoreExitCode.Failure;
        }
    }

    internal static async Task<UninstallConfigRestoreExitCode> RunAsync(
        AppPaths paths,
        string credentialHelperPath,
        string switchMutexName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHelperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(switchMutexName);

        using var switchSemaphore = new Semaphore(1, 1, switchMutexName);
        var ownsSemaphore = false;
        try
        {
            ownsSemaphore = switchSemaphore.WaitOne(0);
            if (!ownsSemaphore)
            {
                return UninstallConfigRestoreExitCode.SwitchInProgress;
            }

            var configService = new DeepSeekCodexConfigService(
                new DeepSeekCodexConfigOptions(
                    paths.CodexConfigFile,
                    paths.DeepSeekModelCatalogFile,
                    paths.DeepSeekConfigStateFile,
                    credentialHelperPath));
            if (configService.IsApplied)
            {
                var restore = await configService.RestoreAsync(cancellationToken);
                if (restore.Status == DeepSeekConfigChangeStatus.Conflict)
                {
                    return UninstallConfigRestoreExitCode.Conflict;
                }

                if (restore.Status is not (
                        DeepSeekConfigChangeStatus.Restored or
                        DeepSeekConfigChangeStatus.NotApplied))
                {
                    return UninstallConfigRestoreExitCode.Failure;
                }
            }

            if (configService.IsApplied ||
                await configService.HasManagedProviderDependencyAsync(
                    cancellationToken))
            {
                return UninstallConfigRestoreExitCode.UnsafeConfiguration;
            }

            return UninstallConfigRestoreExitCode.Success;
        }
        catch
        {
            return UninstallConfigRestoreExitCode.Failure;
        }
        finally
        {
            if (ownsSemaphore)
            {
                switchSemaphore.Release();
            }
        }
    }
}
