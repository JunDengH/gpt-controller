using GptController.Credentials;

namespace GptController.CredentialHelper;

public static class CredentialHelperRunner
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<DeepSeekCredentialStore>? storeFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (!IsGetDeepSeekTokenCommand(arguments))
        {
            await standardError.WriteLineAsync(
                "Usage: get-token --provider deepseek");
            return 2;
        }

        try
        {
            var store = (storeFactory ?? (() => new DeepSeekCredentialStore()))();
            var token = await store.ReadAsync(cancellationToken);
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
                "The DeepSeek API credential is unavailable.");
            return 1;
        }
    }

    private static bool IsGetDeepSeekTokenCommand(IReadOnlyList<string> arguments) =>
        arguments.Count == 3 &&
        string.Equals(arguments[0], "get-token", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[1], "--provider", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            arguments[2],
            ApplicationDataLayout.DeepSeekProvider,
            StringComparison.OrdinalIgnoreCase);
}
