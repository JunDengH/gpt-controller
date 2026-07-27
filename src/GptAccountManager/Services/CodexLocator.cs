using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GptAccountManager.Infrastructure;

namespace GptAccountManager.Services;

public sealed record CodexInstallation(
    string CodexExecutable,
    string? ChatGptExecutable,
    string Aumid);

public sealed record CodexRuntimeManifest(
    string SourcePath,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    string FileVersion,
    string ExecutablePath);

public interface ICodexRuntimeLocator
{
    Task<CodexInstallation> LocateAsync(
        CancellationToken cancellationToken = default);
}

public sealed class CodexLocator : ICodexRuntimeLocator
{
    public const string ChatGptAumid = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CodexInstallation? _cached;

    public CodexLocator(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<CodexInstallation> LocateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cached is not null && File.Exists(_cached.CodexExecutable))
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && File.Exists(_cached.CodexExecutable))
            {
                return _cached;
            }

            var candidates = new List<(string Codex, string? ChatGpt)>();
            AddRunningAppCandidates(candidates);
            AddWindowsAppsCandidates(candidates);

            var packageLocation = await TryReadPackageLocationAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(packageLocation))
            {
                AddInstallLocationCandidates(candidates, packageLocation);
            }

            foreach (var path in EnumeratePathCandidates("codex.exe"))
            {
                candidates.Add((path, null));
            }

            var selected = candidates.FirstOrDefault(candidate => File.Exists(candidate.Codex));
            if (string.IsNullOrWhiteSpace(selected.Codex))
            {
                throw new FileNotFoundException(
                    "找不到官方 codex.exe。请确认已安装并至少启动过一次 ChatGPT Windows 客户端。");
            }

            var sourceCodex = Path.GetFullPath(selected.Codex);
            var runnableCodex = await EnsureRunnableExecutableAsync(
                sourceCodex,
                cancellationToken);
            _cached = new CodexInstallation(
                runnableCodex,
                string.IsNullOrWhiteSpace(selected.ChatGpt) || !File.Exists(selected.ChatGpt)
                    ? null
                    : Path.GetFullPath(selected.ChatGpt),
                ChatGptAumid);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> EnsureRunnableExecutableAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!sourcePath.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        var runtimeRoot = Path.Combine(_paths.Runtime, "codex");
        Directory.CreateDirectory(runtimeRoot);
        var sourceInfo = new FileInfo(sourcePath);
        var rawVersion = FileVersionInfo.GetVersionInfo(sourcePath).FileVersion ?? "unknown";
        var manifestPath = Path.Combine(runtimeRoot, "runtime.json");
        var cachedRuntime = await TryReadRuntimeManifestAsync(
            manifestPath,
            cancellationToken);
        if (cachedRuntime is not null &&
            string.Equals(
                cachedRuntime.SourcePath,
                sourcePath,
                StringComparison.OrdinalIgnoreCase) &&
            cachedRuntime.SourceLength == sourceInfo.Length &&
            cachedRuntime.SourceLastWriteUtcTicks == sourceInfo.LastWriteTimeUtc.Ticks &&
            string.Equals(cachedRuntime.FileVersion, rawVersion, StringComparison.Ordinal) &&
            File.Exists(cachedRuntime.ExecutablePath) &&
            new FileInfo(cachedRuntime.ExecutablePath).Length == sourceInfo.Length)
        {
            return cachedRuntime.ExecutablePath;
        }

        var stagingPath = Path.Combine(runtimeRoot, $".codex-{Guid.NewGuid():N}.tmp");
        string hash;
        try
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var destination = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    incrementalHash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            hash = Convert.ToHexString(incrementalHash.GetHashAndReset())
                .ToLowerInvariant()[..16];
            var safeVersion = string.Concat(rawVersion.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-'
                    ? character
                    : '-'));
            var destinationDirectory = Path.Combine(runtimeRoot, $"{safeVersion}-{hash}");
            var destinationPath = Path.Combine(destinationDirectory, "codex.exe");
            Directory.CreateDirectory(destinationDirectory);

            if (File.Exists(destinationPath) &&
                new FileInfo(destinationPath).Length == new FileInfo(stagingPath).Length)
            {
                AtomicFile.TryDelete(stagingPath);
            }
            else
            {
                File.Move(stagingPath, destinationPath, overwrite: true);
            }

            var manifest = new CodexRuntimeManifest(
                sourcePath,
                sourceInfo.Length,
                sourceInfo.LastWriteTimeUtc.Ticks,
                rawVersion,
                destinationPath);
            await AtomicFile.WriteAllBytesAsync(
                manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest),
                cancellationToken);
            return destinationPath;
        }
        finally
        {
            AtomicFile.TryDelete(stagingPath);
        }
    }

    private static async Task<CodexRuntimeManifest?> TryReadRuntimeManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            return await JsonSerializer.DeserializeAsync<CodexRuntimeManifest>(
                stream,
                cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static void AddRunningAppCandidates(
        ICollection<(string Codex, string? ChatGpt)> candidates)
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var chatGpt = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(chatGpt))
                {
                    continue;
                }

                var appDirectory = Path.GetDirectoryName(chatGpt);
                if (appDirectory is null)
                {
                    continue;
                }

                candidates.Add((
                    Path.Combine(appDirectory, "resources", "codex.exe"),
                    chatGpt));
            }
            catch
            {
                // Access to another process can be denied. Continue with package discovery.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void AddWindowsAppsCandidates(
        ICollection<(string Codex, string? ChatGpt)> candidates)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         windowsApps,
                         "OpenAI.Codex_*_x64__2p2nqsd0c76g0")
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                AddInstallLocationCandidates(candidates, directory);
            }
        }
        catch
        {
            // The WindowsApps directory may deny enumeration. PowerShell fallback follows.
        }
    }

    private static void AddInstallLocationCandidates(
        ICollection<(string Codex, string? ChatGpt)> candidates,
        string installLocation)
    {
        candidates.Add((
            Path.Combine(installLocation, "app", "resources", "codex.exe"),
            Path.Combine(installLocation, "app", "ChatGPT.exe")));
        candidates.Add((
            Path.Combine(installLocation, "resources", "codex.exe"),
            Path.Combine(installLocation, "ChatGPT.exe")));
    }

    private static async Task<string?> TryReadPackageLocationAsync(
        CancellationToken cancellationToken)
    {
        Process? process = null;
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
            startInfo.ArgumentList.Add(
                "(Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1 -ExpandProperty InstallLocation)");
            process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? output
                : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation still propagates even if cleanup races process exit.
            }

            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executable);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }
}
