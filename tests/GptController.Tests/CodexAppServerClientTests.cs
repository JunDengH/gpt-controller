using System.Diagnostics;
using GptController.Infrastructure;
using GptController.Services;

namespace GptController.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task UnresponsiveProcessIsKilledAfterGracePeriod()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GptController.Tests",
            Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(testRoot, testRoot);
        var logger = new RedactingLogger(paths);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start test process.");

        try
        {
            var stopwatch = Stopwatch.StartNew();

            await CodexAppServerClient.StopProcessAsync(process, logger);

            stopwatch.Stop();
            Assert.True(process.HasExited);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 400, 2500);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
