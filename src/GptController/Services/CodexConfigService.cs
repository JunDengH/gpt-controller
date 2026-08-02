using System.Text.RegularExpressions;
using GptController.Infrastructure;

namespace GptController.Services;

public sealed partial class CodexConfigService
{
    private readonly AppPaths _paths;

    public CodexConfigService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<bool> IsFileStoreCompatibleAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.CodexConfigFile))
        {
            return true;
        }

        var content = await File.ReadAllTextAsync(_paths.CodexConfigFile, cancellationToken);
        var match = CredentialStoreRegex().Match(content);
        if (!match.Success)
        {
            return true;
        }

        var mode = match.Groups["mode"].Value;
        return mode.Equals("file", StringComparison.OrdinalIgnoreCase) ||
               mode.Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    public async Task EnableFileStoreAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.CodexHome);
        var content = File.Exists(_paths.CodexConfigFile)
            ? await File.ReadAllTextAsync(_paths.CodexConfigFile, cancellationToken)
            : string.Empty;

        if (File.Exists(_paths.CodexConfigFile))
        {
            var backupPath = _paths.CodexConfigFile + $".gpt-controller-{DateTimeOffset.Now:yyyyMMddHHmmss}.bak";
            File.Copy(_paths.CodexConfigFile, backupPath, overwrite: false);
        }

        var replacement = "cli_auth_credentials_store = \"file\"";
        var updated = CredentialStoreRegex().IsMatch(content)
            ? CredentialStoreRegex().Replace(content, replacement, 1)
            : string.IsNullOrWhiteSpace(content)
                ? replacement + Environment.NewLine
                : content.TrimEnd() + Environment.NewLine + replacement + Environment.NewLine;
        await AtomicFile.WriteAllTextAsync(_paths.CodexConfigFile, updated, cancellationToken);
    }

    [GeneratedRegex(
        @"(?m)^\s*cli_auth_credentials_store\s*=\s*[""'](?<mode>[^""']+)[""']\s*(?:#.*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialStoreRegex();
}
