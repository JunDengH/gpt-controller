using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Mvvm;
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

    [Fact]
    public async Task SuccessfulRefreshRebuildsUnifiedConnectionIndex()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var vault = new ProfileVault(paths);
        var oauth = await vault.UpsertProfileAsync(new AccountProfile
        {
            Nickname = "Team",
            Email = "team@example.com",
            AccountId = "workspace-team",
            MembershipPlan = MembershipPlan.Business,
            Ownership = AccountOwnership.Organization(
                "workspace-team",
                "示例科技")
        });

        var deepSeekCredentials = new DeepSeekCredentialStore(paths.Root);
        var deepSeekStore = new DeepSeekConnectionStore(
            paths,
            deepSeekCredentials);
        var deepSeek = await deepSeekStore.SaveAsync(
            new DeepSeekConnection { Nickname = "DeepSeek V4" },
            "sk-deepseek-view-model-refresh-1234");

        var qwenCredentials = new QwenCredentialStore(paths.Root);
        var qwenStore = new QwenConnectionStore(paths, qwenCredentials);
        var qwen = await qwenStore.SaveAsync(
            new QwenConnection
            {
                Model = "qwen3-coder-plus",
                Region = QwenRegion.Virginia,
                Models = [new ApiModelDescriptor { Id = "qwen3-coder-plus" }]
            },
            "sk-qwen-view-model-refresh-1234");

        var connectionIndexStore = new ConnectionIndexStore(paths);
        await connectionIndexStore.SaveProjectionAsync([], null, null);
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
            deepSeekStore: deepSeekStore,
            deepSeekCredentialStore: deepSeekCredentials,
            deepSeekApiClient: new SuccessfulDeepSeekApiClient(),
            codexVersionService: null!,
            connectionSwitchCoordinator: null!,
            connectionIndexStore: connectionIndexStore,
            credentialHelperPath: "helper.exe",
            qwenStore: qwenStore,
            qwenCredentialStore: qwenCredentials,
            qwenApiClient: null!);
        var card = new AccountCardViewModel(deepSeek);
        viewModel.Accounts.Add(card);

        await ExecuteAsync(viewModel.RefreshAccountCommand, card);

        var projection = await connectionIndexStore.LoadAsync();
        Assert.NotNull(projection);
        var projectedOauth = Assert.Single(projection.ChatGptConnections);
        Assert.Equal(oauth.Id, projectedOauth.ProfileId);
        Assert.Equal("示例科技", projectedOauth.Ownership.DisplayName);
        Assert.Equal(
            DeepSeekConnectionStatus.Available,
            projection.DeepSeekConnection?.Status);
        Assert.Contains(
            projection.ApiConnections,
            connection => connection.Provider == ConnectionProvider.DeepSeek);
        Assert.Contains(
            projection.ApiConnections,
            connection =>
                connection.Provider == ConnectionProvider.Qwen &&
                connection.Model == qwen.Model);
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

    private sealed class SuccessfulDeepSeekApiClient : IDeepSeekApiClient
    {
        public Task<DeepSeekBalanceSnapshot> GetBalanceAsync(
            string apiKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeepSeekBalanceSnapshot
            {
                IsAvailable = true,
                Balances =
                [
                    new DeepSeekBalanceInfo
                    {
                        Currency = "CNY",
                        TotalBalance = 12.5m
                    }
                ]
            });

        public Task<DeepSeekResponseTestResult> TestResponseAsync(
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeepSeekResponseTestResult> TestResponseAsync(
            string apiKey,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static async Task ExecuteAsync(
        AsyncRelayCommand<AccountCardViewModel> command,
        AccountCardViewModel account)
    {
        var started = false;
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!command.CanExecute(account))
            {
                started = true;
            }
            else if (started)
            {
                completed.TrySetResult();
            }
        };
        command.CanExecuteChanged += handler;
        try
        {
            command.Execute(account);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            command.CanExecuteChanged -= handler;
        }
    }
}
