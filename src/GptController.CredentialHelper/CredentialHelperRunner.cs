using GptController.Credentials;

namespace GptController.CredentialHelper;

public static class CredentialHelperRunner
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<DeepSeekCredentialStore>? storeFactory = null,
        CancellationToken cancellationToken = default,
        Func<QwenCredentialStore>? qwenStoreFactory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (!TryGetProvider(arguments, out var provider))
        {
            await standardError.WriteLineAsync(
                "Usage: get-token --provider deepseek|qwen");
            return 2;
        }

        try
        {
            var token = string.Equals(
                    provider,
                    ApplicationDataLayout.QwenProvider,
                    StringComparison.OrdinalIgnoreCase)
                ? await (qwenStoreFactory ?? (() => new QwenCredentialStore()))()
                    .ReadAsync(cancellationToken)
                : await (storeFactory ?? (() => new DeepSeekCredentialStore()))()
                    .ReadAsync(cancellationToken);
            await standardOutput.WriteLineAsync(token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Credential retrieval was cancelled.");
            return 3;
        }
        catch (Exception exception) when (
            exception is CredentialStoreException or IOException or UnauthorizedAccessException)
        {
            // The exception text is intentionally not forwarded. It can contain a
            // path, but callers only need a stable, non-sensitive failure message.
            await standardError.WriteLineAsync(
                string.Equals(
                    provider,
                    ApplicationDataLayout.QwenProvider,
                    StringComparison.OrdinalIgnoreCase)
                    ? "The Qwen API credential is unavailable."
                    : "The DeepSeek API credential is unavailable.");
            return 1;
        }
    }

    private static bool TryGetProvider(
        IReadOnlyList<string> arguments,
        out string provider)
    {
        provider = arguments.Count == 3 ? arguments[2] : string.Empty;
        return arguments.Count == 3 &&
               string.Equals(arguments[0], "get-token", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(arguments[1], "--provider", StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(
                    provider,
                    ApplicationDataLayout.DeepSeekProvider,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    provider,
                    ApplicationDataLayout.QwenProvider,
                    StringComparison.OrdinalIgnoreCase));
    }
}
