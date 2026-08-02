namespace GptController.Services;

public interface IChatGptProcessController
{
    bool IsChatGptRunning();
    Task<bool> StopChatGptAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
        CancellationToken cancellationToken = default);
    Task<bool> LaunchChatGptAsync(CancellationToken cancellationToken = default);
}
