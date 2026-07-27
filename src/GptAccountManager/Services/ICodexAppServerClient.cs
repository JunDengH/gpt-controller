using System.Text.Json;
using GptAccountManager.Infrastructure;

namespace GptAccountManager.Services;

public interface ICodexAppServerClient : IAsyncDisposable
{
    Task<AccountReadMetadata> ReadAccountAsync(
        CancellationToken cancellationToken = default);

    Task<JsonElement> ReadRateLimitsAsync(
        CancellationToken cancellationToken = default);

    Task<LoginStartResult> StartChatGptLoginAsync(
        CancellationToken cancellationToken = default);

    Task WaitForLoginCompletedAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface ICodexAppServerClientFactory
{
    Task<ICodexAppServerClient> StartAsync(
        string codexExecutable,
        string codexHome,
        RedactingLogger logger,
        CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerClientFactory : ICodexAppServerClientFactory
{
    public async Task<ICodexAppServerClient> StartAsync(
        string codexExecutable,
        string codexHome,
        RedactingLogger logger,
        CancellationToken cancellationToken = default) =>
        await CodexAppServerClient.StartAsync(
            codexExecutable,
            codexHome,
            logger,
            cancellationToken);
}
