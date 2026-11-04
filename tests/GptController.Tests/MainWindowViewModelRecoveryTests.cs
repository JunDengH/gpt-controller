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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRefreshTakesOverBlockedBackgroundRefresh(bool refreshAll)
    {
        var client = new SuccessfulDeepSeekApiClient { BlockFirstRequest = true };
        using var viewModel = await CreateRefreshViewModelAsync(client);
        var card = Assert.Single(viewModel.Accounts);
        var previous = card.LastSyncedAt;
        var background = viewModel.RefreshAllAsync(true, QuotaRefreshReason.Automatic);
        await client.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.RefreshAllCommand.CanExecute(null));
        Assert.True(viewModel.RefreshAccountCommand.CanExecute(card));
        Assert.Contains("正在刷新", viewModel.LatestSyncText);
        Assert.Equal("正在刷新…", card.DetailUpdatedText);
        Assert.Equal(previous, card.LastSyncedAt);

        if (refreshAll)
            await viewModel.RefreshAllAsync(false, QuotaRefreshReason.Manual);
        else
            await ExecuteAsync(viewModel.RefreshAccountCommand, card);
        await background.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, client.BalanceRequests);
        Assert.Equal(1, client.MaximumActiveRequests);
        Assert.True(card.HasFreshQuota);
        Assert.True(card.LastSyncedAt > previous);
        Assert.False(card.IsRefreshing);
        Assert.False(viewModel.IsWorking);
        Assert.DoesNotContain("正在刷新", viewModel.LatestSyncText);
        Assert.Contains("已更新", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ProviderFailureDoesNotStopLaterAccountsOrReportStaleDataAsUpdated()
    {
        var client = new SuccessfulDeepSeekApiClient { FailBalance = true };
        using var viewModel = await CreateRefreshViewModelAsync(client);
        var paths = new AppPaths(_root, _root);
        var previous = DateTimeOffset.UtcNow.AddDays(-1);
        var profile = await new ProfileVault(paths).UpsertProfileAsync(new AccountProfile
        {
            Nickname = "Missing credential", AccountId = "missing", Email = "test@example.com",
            Quota = new QuotaSnapshot
            {
                FetchedAt = previous, Status = QuotaStatus.Fresh, RemainingPercent = 50
            }
        });
        var card = new AccountCardViewModel(profile);
        viewModel.Accounts.Add(card);

        await viewModel.RefreshAllAsync(true, QuotaRefreshReason.Automatic);

        Assert.Equal("invalid_auth", card.Profile.Quota?.ErrorCode);
        Assert.Equal(previous, card.LastSyncedAt);
        Assert.StartsWith("上次 ", card.DetailUpdatedText);
        Assert.Equal("需要重新登录", card.QuotaStatusText);
        Assert.Contains("0 个已更新，2 个未更新", viewModel.StatusMessage);
        Assert.False(viewModel.IsWorking);
    }

    private async Task<MainWindowViewModel> CreateRefreshViewModelAsync(SuccessfulDeepSeekApiClient client)
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var vault = new ProfileVault(paths);
        var credentials = new DeepSeekCredentialStore(paths.Root);
        var store = new DeepSeekConnectionStore(paths, credentials);
        var connection = await store.SaveAsync(new DeepSeekConnection
        {
            ModelsFetchedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Models = [new ApiModelDescriptor { Id = DeepSeekDefaults.Model }]
        }, "sk-refresh-test-1234");
        var process = new PassiveProcessController();
        var gate = new OperationGate();
        var logger = new RedactingLogger(paths);
        var viewModel = new MainWindowViewModel(
            vault: vault, settingsService: null!, configService: null!, importService: null!,
            oauthService: null!,
            quotaService: new QuotaService(paths, vault, null!, null!, process,
                new AccountMetadataService(), new QuotaParser(), gate, logger),
            switchCoordinator: new SwitchCoordinator(paths, vault, process, gate, logger),
            processController: process, dialogs: null!, deepSeekStore: store,
            deepSeekCredentialStore: credentials, deepSeekApiClient: client,
            codexVersionService: null!, connectionSwitchCoordinator: null!,
            connectionIndexStore: new ConnectionIndexStore(paths), credentialHelperPath: "helper.exe");
        viewModel.Accounts.Add(new AccountCardViewModel(connection));
        return viewModel;
    }

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
        private int _activeRequests;
        public bool BlockFirstRequest { get; init; }
        public bool FailBalance { get; init; }
        public int BalanceRequests { get; private set; }
        public int MaximumActiveRequests { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ApiModelDescriptor>> GetModelsAsync(string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiModelDescriptor>>([new ApiModelDescriptor { Id = DeepSeekDefaults.Model }]);
        public async Task<DeepSeekBalanceSnapshot> GetBalanceAsync(
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            BalanceRequests++;
            _activeRequests++;
            MaximumActiveRequests = Math.Max(MaximumActiveRequests, _activeRequests);
            try
            {
                if (BlockFirstRequest && BalanceRequests == 1)
                {
                    ReadStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                if (FailBalance)
                    throw new DeepSeekApiException(DeepSeekApiErrorKind.Timeout, "Timed out");
                return new DeepSeekBalanceSnapshot
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
                };
            }
            finally
            {
                _activeRequests--;
            }
        }

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
