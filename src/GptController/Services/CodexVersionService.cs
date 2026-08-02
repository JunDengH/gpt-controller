using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GptController.Services;

public sealed record CodexVersionStatus(
    Version? InstalledVersion,
    Version MinimumVersion,
    string DisplayText)
{
    public bool IsSupported => InstalledVersion is not null && InstalledVersion >= MinimumVersion;
}

public sealed partial class CodexVersionService
{
    public static readonly Version MinimumDeepSeekVersion = new(0, 146, 0);

    private readonly ICodexRuntimeLocator _locator;

    public CodexVersionService(ICodexRuntimeLocator locator)
    {
        _locator = locator;
    }

    public async Task<CodexVersionStatus> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var installation = await _locator.LocateAsync(cancellationToken);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = installation.CodexExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var display = string.IsNullOrWhiteSpace(output) ? error : output;
        var version = Parse(display);
        return new CodexVersionStatus(version, MinimumDeepSeekVersion, display);
    }

    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionRegex().Match(value);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version)
            ? version
            : null;
    }

    private static void TryKill(Process process)
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
            // The original timeout or cancellation remains the useful failure.
        }
    }

    [GeneratedRegex(@"(?<!\d)(\d+\.\d+\.\d+)(?:[-+][0-9A-Za-z.-]+)?")]
    private static partial Regex VersionRegex();
}
