using System.Diagnostics;
using System.Text.Json;
using GptAccountManager.Infrastructure;

namespace GptAccountManager.Services;

public sealed class ChatGptProcessController : IChatGptProcessController
{
    private readonly ICodexRuntimeLocator _locator;
    private readonly RedactingLogger _logger;

    public ChatGptProcessController(
        ICodexRuntimeLocator locator,
        RedactingLogger logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public bool IsChatGptRunning()
    {
        var processes = Process.GetProcessesByName("ChatGPT");
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return !process.HasExited;
                }
                catch
                {
                    return true;
                }
            });
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<bool> StopChatGptAsync(
        CancellationToken cancellationToken = default)
    {
        var processes = Process.GetProcessesByName("ChatGPT");
        if (processes.Length == 0)
        {
            return true;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                    // Continue to the bounded force-close stage.
                }
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (processes.All(HasExitedSafely))
                {
                    return true;
                }

                await Task.Delay(200, cancellationToken);
            }

            foreach (var process in processes.Where(process => !HasExitedSafely(process)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception)
                {
                    await _logger.ErrorAsync("process.stop", exception);
                }
            }

            await Task.WhenAll(processes.Select(process => WaitForExitSafelyAsync(
                process,
                cancellationToken)));
            return processes.All(HasExitedSafely);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
        CancellationToken cancellationToken = default)
    {
        const string command =
            """
            $owners = @('ChatGPT.exe', 'Code.exe', 'Code - Insiders.exe', 'VSCodium.exe')
            $all = Get-CimInstance Win32_Process
            $byId = @{}
            foreach ($item in $all) { $byId[[int]$item.ProcessId] = $item }
            $result = foreach ($item in $all) {
              if ($item.Name -ne 'codex.exe' -or $item.CommandLine -notmatch '(^|\s)app-server(\s|$)') { continue }
              $cursor = $item
              $owner = $null
              for ($depth = 0; $depth -lt 6 -and $cursor; $depth++) {
                $cursor = $byId[[int]$cursor.ParentProcessId]
                if ($cursor -and $owners -contains $cursor.Name) {
                  $owner = $cursor.Name
                  break
                }
              }
              if ($owner) {
                [pscustomobject]@{
                  ProcessId = $item.ProcessId
                  ParentProcessId = $item.ParentProcessId
                  OwnerName = $owner
                }
              }
            }
            if ($result) { @($result) | ConvertTo-Json -Compress }
            """;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            try
            {
                await process
                    .WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TimeoutException)
            {
                KillProcessTreeBestEffort(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                KillProcessTreeBestEffort(process);
                throw;
            }

            var output = (await outputTask).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return [];
            }

            using var document = JsonDocument.Parse(output);
            var rows = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList()
                : [document.RootElement.Clone()];
            return rows
                .Select(row => row.TryGetProperty("ProcessId", out var id)
                    ? $"Codex app-server（PID {id.GetInt32()}，{ReadOwner(row)}）"
                    : "Codex app-server")
                .ToList();
        }
        catch (TimeoutException exception)
        {
            await _logger.WarningAsync(
                "process.inspect",
                $"Inspecting external app-server processes timed out: {exception.Message}");
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.WarningAsync(
                "process.inspect",
                $"Could not inspect external app-server processes: {exception.Message}");
            return [];
        }
    }

    private static string ReadOwner(JsonElement row)
    {
        return row.TryGetProperty("OwnerName", out var owner) &&
               owner.ValueKind == JsonValueKind.String
            ? owner.GetString() ?? "官方客户端"
            : "官方客户端";
    }

    public async Task<bool> LaunchChatGptAsync(
        CancellationToken cancellationToken = default)
    {
        var installation = await _locator.LocateAsync(cancellationToken);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{installation.Aumid}",
                UseShellExecute = true
            });

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsChatGptRunning())
                {
                    return true;
                }

                await Task.Delay(300, cancellationToken);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("process.launch", exception);
            return false;
        }
    }

    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static void KillProcessTreeBestEffort(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The caller handles the timeout or cancellation that led here.
        }
    }

    private static async Task WaitForExitSafelyAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Caller verifies the final process state.
        }
    }
}
