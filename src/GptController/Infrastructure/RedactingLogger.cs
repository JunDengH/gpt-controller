using System.Text.RegularExpressions;

namespace GptController.Infrastructure;

public sealed partial class RedactingLogger
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public RedactingLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task InfoAsync(string category, string message) =>
        await WriteAsync("INFO", category, message);

    public async Task WarningAsync(string category, string message) =>
        await WriteAsync("WARN", category, message);

    public async Task ErrorAsync(string category, Exception exception) =>
        await WriteAsync("ERROR", category, exception.GetType().Name + ": " + exception.Message);

    private async Task WriteAsync(string level, string category, string message)
    {
        var sanitized = Redact(message);
        var line = $"{DateTimeOffset.Now:O}\t{level}\t{category}\t{sanitized}{Environment.NewLine}";
        var logPath = Path.Combine(_paths.Logs, $"app-{DateTimeOffset.Now:yyyyMMdd}.log");

        await _writeGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            await File.AppendAllTextAsync(logPath, line);
        }
        catch
        {
            // Logging must never break an account operation.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static string Redact(string value)
    {
        var result = JwtRegex().Replace(value, "<redacted-jwt>");
        result = BearerRegex().Replace(result, "Bearer <redacted>");
        result = ApiKeyRegex().Replace(result, "<redacted-api-key>");
        result = EmailRegex().Replace(result, "<redacted-email>");
        result = RefreshTokenRegex().Replace(result, "\"refresh_token\":\"<redacted>\"");
        return result.Replace('\r', ' ').Replace('\n', ' ');
    }

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{8,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"""refresh_token""\s*:\s*""[^""]+""", RegexOptions.IgnoreCase)]
    private static partial Regex RefreshTokenRegex();
}
