using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;
using GptController.ViewModels;

namespace GptController.Tests;

public sealed class MainWindowViewModelRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"view-model-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task PendingSwitchJournalDisablesManualConnectionRefresh()
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.TransactionFile, "pending");
        var vault = new ProfileVault(paths);
        var process = new PassiveProcessController();
        var switchCoordinator = new SwitchCoordinator(
            paths,
            vault,
            process,
            new OperationGate(),
            new RedactingLogger(paths));
        using var viewModel = new MainWindowViewModel(
            vault: vault,
            settingsService: null!,
            configService: null!,
            importService: null!,
            oauthService: null!,
            quotaService: null!,
            switchCoordinator: switchCoordinator,
            processController: process,
            dialogs: null!,
            deepSeekStore: null!,
            deepSeekCredentialStore: null!,
            deepSeekApiClient: null!,
            codexVersionService: null!,
            connectionSwitchCoordinator: null!,
            connectionIndexStore: null!,
            credentialHelperPath: "helper.exe");
        var account = new AccountCardViewModel(
            new AccountProfile
            {
                Nickname = "ChatGPT",
                Email = "test@example.com",
                AccountId = "account",
                IsActive = false,
                Ownership = AccountOwnership.Personal
            });

        Assert.False(viewModel.RefreshAllCommand.CanExecute(null));
        Assert.False(viewModel.RefreshAccountCommand.CanExecute(account));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private sealed class PassiveProcessController : IChatGptProcessController
    {
        public bool IsChatGptRunning() => false;

        public Task<bool> StopChatGptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> LaunchChatGptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
