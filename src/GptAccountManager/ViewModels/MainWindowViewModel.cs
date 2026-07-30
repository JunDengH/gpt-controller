using System.Collections.ObjectModel;
using System.Windows.Threading;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;
using GptAccountManager.Mvvm;
using GptAccountManager.Services;

namespace GptAccountManager.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ProfileVault _vault;
    private readonly SettingsService _settingsService;
    private readonly CodexConfigService _configService;
    private readonly CurrentAccountImportService _importService;
    private readonly OAuthAccountService _oauthService;
    private readonly QuotaService _quotaService;
    private readonly SwitchCoordinator _switchCoordinator;
    private readonly IChatGptProcessController _processController;
    private readonly DialogService _dialogs;
    private readonly bool _isUiPreview;
    private readonly DispatcherTimer _quotaTimer = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private AppSettings _settings = new();
    private bool _isBusy;
    private bool _isRefreshingAll;
    private bool _isSettingsPage;
    private int _quotaRefreshMinutes = 15;
    private bool _closeToTray = true;
    private bool _startMinimized;
    private string _statusMessage = "准备就绪";
    private CancellationTokenSource? _refreshCts;
    private Task _refreshTask = Task.CompletedTask;
    private bool _disposed;

    public MainWindowViewModel(
        ProfileVault vault,
        SettingsService settingsService,
        CodexConfigService configService,
        CurrentAccountImportService importService,
        OAuthAccountService oauthService,
        QuotaService quotaService,
        SwitchCoordinator switchCoordinator,
        IChatGptProcessController processController,
        DialogService dialogs,
        bool isUiPreview = false)
    {
        _vault = vault;
        _settingsService = settingsService;
        _configService = configService;
        _importService = importService;
        _oauthService = oauthService;
        _quotaService = quotaService;
        _switchCoordinator = switchCoordinator;
        _processController = processController;
        _dialogs = dialogs;
        _isUiPreview = isUiPreview;

        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync, () => !IsBusy);
        ImportCurrentCommand = new AsyncRelayCommand(ImportCurrentAsync, () => !IsBusy);
        RefreshAllCommand = new AsyncRelayCommand(
            RefreshAllAsync,
            () => !IsBusy && !HasActiveRefresh);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        ShowAccountsCommand = new RelayCommand(ShowAccountsPage);
        ShowSettingsCommand = new RelayCommand(ShowSettingsPage);
        SwitchAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            SwitchAccountAsync,
            account => !IsBusy && !account.IsActive);
        RefreshAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            RefreshAccountAsync,
            account => !IsBusy && !HasActiveRefresh && !account.IsRefreshing);
        RenameAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            RenameAccountAsync,
            _ => !IsBusy);
        DeleteAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            DeleteAccountAsync,
            account => !IsBusy && account.CanDelete);

        _quotaTimer.Tick += async (_, _) =>
        {
            if (!IsBusy && !HasActiveRefresh)
            {
                await RefreshAllAsync(
                    silent: true,
                    QuotaRefreshReason.Automatic);
            }
        };
    }

    public ObservableCollection<AccountCardViewModel> Accounts { get; } = [];

    public AsyncRelayCommand AddAccountCommand { get; }
    public AsyncRelayCommand ImportCurrentCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand ShowAccountsCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> SwitchAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> RefreshAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> RenameAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> DeleteAccountCommand { get; }

    public event EventHandler? AccountsChanged;

    public bool IsAccountsPage => !_isSettingsPage;

    public bool IsSettingsPage => _isSettingsPage;

    public int QuotaRefreshMinutes
    {
        get => _quotaRefreshMinutes;
        set => SetProperty(ref _quotaRefreshMinutes, Math.Clamp(value, 5, 120));
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                NotifyCommands();
            }
        }
    }

    public bool IsRefreshingAll
    {
        get => _isRefreshingAll;
        private set
        {
            if (SetProperty(ref _isRefreshingAll, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                NotifyCommands();
            }
        }
    }

    public bool IsWorking => IsBusy || IsRefreshingAll || HasActiveRefresh;

    private bool HasActiveRefresh => !_refreshTask.IsCompleted;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync(_lifetimeCts.Token);
        QuotaRefreshMinutes = _settings.QuotaRefreshMinutes;
        CloseToTray = _settings.CloseToTray;
        StartMinimized = _settings.StartMinimized;
        _quotaTimer.Interval = TimeSpan.FromMinutes(
            QuotaRefreshMinutes);

        if (_isUiPreview)
        {
            LoadUiPreviewAccounts();
            StatusMessage = "界面预览模式 · 账号操作不会写入本地数据";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "正在检查恢复状态…";
            var recovered = await _switchCoordinator.RecoverPendingTransactionAsync(
                _lifetimeCts.Token);
            if (recovered)
            {
                StatusMessage = "已恢复上次未完成的账号切换";
            }

            await ReloadAccountsAsync(_lifetimeCts.Token);
            if (Accounts.Count == 0 &&
                _importService.HasLiveAccount &&
                _dialogs.Ask(
                    "导入当前账号",
                    "检测到 ChatGPT 当前登录账号。是否将它安全导入账号管理器？"))
            {
                await ImportCurrentCoreAsync(_lifetimeCts.Token);
            }

            if (!recovered)
            {
                StatusMessage = Accounts.Count > 0
                    ? "已加载账号数据"
                    : "准备就绪";
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels initialization without showing an error.
        }
        catch (Exception exception)
        {
            StatusMessage = "初始化失败";
            _dialogs.Error("初始化失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
            if (!_disposed)
            {
                _quotaTimer.Start();
            }
        }
    }

    private async Task AddAccountAsync()
    {
        if (_isUiPreview)
        {
            StatusMessage = "界面预览模式不会启动 OAuth";
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            if (!await EnsureFileStoreAsync(_lifetimeCts.Token))
            {
                return;
            }

            var account = await _oauthService.AddAccountAsync(
                message => DispatchStatus(message),
                _lifetimeCts.Token);
            StatusMessage = $"已添加 {account.Nickname}";
            await ReloadAccountsAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                StatusMessage = "已取消添加账号";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = "添加账号失败";
            _dialogs.Error("添加账号失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportCurrentAsync()
    {
        if (_isUiPreview)
        {
            StatusMessage = "界面预览模式不会导入真实登录态";
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            if (!await EnsureFileStoreAsync(_lifetimeCts.Token))
            {
                return;
            }

            var imported = await ImportCurrentCoreAsync(_lifetimeCts.Token);
            IsBusy = false;
            StartPostSwitchRefresh(imported.Id);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels import without showing an error.
        }
        catch (Exception exception)
        {
            StatusMessage = "导入当前账号失败";
            _dialogs.Error("导入失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<AccountProfile> ImportCurrentCoreAsync(
        CancellationToken cancellationToken)
    {
        StatusMessage = "正在导入当前账号…";
        var imported = await _importService.ImportAsync(cancellationToken);
        StatusMessage = $"已导入 {imported.Nickname}";
        await ReloadAccountsAsync(cancellationToken);
        return imported;
    }

    private Task RefreshAllAsync() => RefreshAllAsync(
        silent: false,
        QuotaRefreshReason.Manual);

    private async Task RefreshAllAsync(
        bool silent,
        QuotaRefreshReason reason)
    {
        if (_isUiPreview)
        {
            LoadUiPreviewAccounts();
            StatusMessage = "预览数据已刷新";
            return;
        }

        if (Accounts.Count == 0)
        {
            return;
        }

        try
        {
            await RunRefreshSessionAsync(async cancellationToken =>
            {
                IsRefreshingAll = true;
                var refreshedCount = 0;
                try
                {
                    if (!silent)
                    {
                        StatusMessage = "正在刷新所有账号额度…";
                    }

                    foreach (var account in Accounts.ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (reason == QuotaRefreshReason.Automatic &&
                            QuotaRefreshPolicy.ShouldSkipAutomatic(
                                account.Profile.Quota))
                        {
                            continue;
                        }

                        await RefreshCardCoreAsync(
                            account,
                            reason,
                            updateGlobalStatus: false,
                            cancellationToken);
                        refreshedCount++;
                    }

                    StatusMessage = refreshedCount == 0
                        ? "已跳过确认需要重新登录的账号"
                        : "额度已更新";
                }
                finally
                {
                    IsRefreshingAll = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A switch, account mutation, or application exit cancels refresh.
        }
        catch (Exception exception)
        {
            StatusMessage = "部分额度刷新失败";
            if (!silent)
            {
                _dialogs.Error("刷新失败", exception.Message);
            }
        }
    }

    private async Task RefreshAccountAsync(AccountCardViewModel account)
    {
        if (_isUiPreview)
        {
            StatusMessage = $"{account.Nickname} 的预览数据已刷新";
            return;
        }

        try
        {
            await RunRefreshSessionAsync(cancellationToken =>
                RefreshCardCoreAsync(
                    account,
                    QuotaRefreshReason.Manual,
                    updateGlobalStatus: true,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // A switch, account mutation, or application exit cancels refresh.
        }
        catch (Exception exception)
        {
            StatusMessage = "额度刷新失败";
            _dialogs.Error("刷新失败", exception.Message);
        }
    }

    private async Task RefreshCardCoreAsync(
        AccountCardViewModel account,
        QuotaRefreshReason reason,
        bool updateGlobalStatus,
        CancellationToken cancellationToken)
    {
        account.IsRefreshing = true;
        NotifyCommands();
        try
        {
            if (updateGlobalStatus)
            {
                StatusMessage = $"正在刷新 {account.Nickname}…";
            }

            var updated = await _quotaService.RefreshAsync(
                account.Id,
                reason,
                cancellationToken);
            account.UpdateProfile(updated);
            AccountsChanged?.Invoke(this, EventArgs.Empty);
            if (updateGlobalStatus)
            {
                StatusMessage = $"{account.Nickname} 已更新";
            }
        }
        finally
        {
            account.IsRefreshing = false;
            NotifyCommands();
        }
    }

    private async Task SwitchAccountAsync(AccountCardViewModel account)
    {
        if (account.IsActive)
        {
            StatusMessage = $"{account.Nickname} 已经是当前账号";
            return;
        }

        if (_isUiPreview)
        {
            var profiles = Accounts
                .Select(item => item.Profile with { IsActive = item.Id == account.Id })
                .OrderByDescending(item => item.IsActive)
                .ThenBy(item => item.Nickname, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Accounts.Clear();
            foreach (var profile in profiles)
            {
                Accounts.Add(new AccountCardViewModel(profile));
            }

            OnPropertyChanged(nameof(Accounts));
            AccountsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = $"预览：已切换到 {account.Nickname}";
            return;
        }

        if (_processController.IsChatGptRunning() &&
            !_dialogs.Confirm(
                "切换账号并重启 ChatGPT",
                $"切换到“{account.Nickname}”需要关闭并重启 ChatGPT。\n\n" +
                "正在运行的任务可能会被中断。默认操作是取消；确认继续吗？"))
        {
            StatusMessage = "已取消账号切换";
            return;
        }

        IsBusy = true;
        var refreshAfterSwitch = false;
        try
        {
            await CancelAndDrainRefreshesAsync();
            var progress = new ImmediateProgress<SwitchStage>(stage =>
                StatusMessage = DescribeSwitchStage(stage, account.Nickname));
            var result = await _switchCoordinator.SwitchAsync(
                account.Id,
                progress,
                _lifetimeCts.Token);
            await ReloadAccountsAsync(_lifetimeCts.Token);
            StatusMessage = result.Message;
            if (!result.IsSuccess)
            {
                _dialogs.Error("账号切换", result.Message);
            }
            else
            {
                refreshAfterSwitch = true;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels switching without showing an error.
        }
        catch (Exception exception)
        {
            StatusMessage = "账号切换失败";
            _dialogs.Error("账号切换失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (refreshAfterSwitch && !_disposed)
        {
            StartPostSwitchRefresh(account.Id);
        }
    }

    private async Task RenameAccountAsync(AccountCardViewModel account)
    {
        if (_isUiPreview)
        {
            StatusMessage = "界面预览模式不会修改账号昵称";
            return;
        }

        var nickname = _dialogs.Prompt("编辑昵称", "账号昵称", account.Nickname);
        if (string.IsNullOrWhiteSpace(nickname) || nickname == account.Nickname)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            var profile = await _vault.GetProfileAsync(
                account.Id,
                _lifetimeCts.Token);
            if (profile is null)
            {
                return;
            }

            await _vault.UpsertProfileAsync(
                profile with { Nickname = nickname.Trim() },
                cancellationToken: _lifetimeCts.Token);
            await ReloadAccountsAsync(_lifetimeCts.Token);
            StatusMessage = "昵称已更新";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels mutation without showing an error.
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAccountAsync(AccountCardViewModel account)
    {
        if (_isUiPreview)
        {
            StatusMessage = "界面预览模式不会删除账号";
            return;
        }

        if (account.IsActive)
        {
            _dialogs.Info("无法删除", "请先切换到其他账号，再删除当前账号。");
            return;
        }

        if (!_dialogs.Confirm(
                "删除账号",
                $"确定删除“{account.Nickname}”吗？\n\n只会删除本软件保存的加密档案，不会退出或注销 OpenAI 账号。"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            await _vault.DeleteProfileAsync(account.Id, _lifetimeCts.Token);
            await ReloadAccountsAsync(_lifetimeCts.Token);
            StatusMessage = "账号档案已删除";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels mutation without showing an error.
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureFileStoreAsync(
        CancellationToken cancellationToken)
    {
        if (await _configService.IsFileStoreCompatibleAsync(cancellationToken))
        {
            return true;
        }

        if (!_dialogs.Confirm(
                "需要文件认证模式",
                "检测到 Codex 正在使用系统钥匙串。本软件需要官方 auth.json 才能安全切换账号。\n\n" +
                "是否备份 config.toml 并切换到 file 模式？"))
        {
            return false;
        }

        await _configService.EnableFileStoreAsync(cancellationToken);
        _dialogs.Info("配置已更新", "已切换到文件认证模式。重新登录后即可管理账号。");
        return true;
    }

    private void ShowAccountsPage()
    {
        if (!_isSettingsPage)
        {
            return;
        }

        _isSettingsPage = false;
        OnPropertyChanged(nameof(IsAccountsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    private void ShowSettingsPage()
    {
        if (_isSettingsPage)
        {
            return;
        }

        _isSettingsPage = true;
        OnPropertyChanged(nameof(IsAccountsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    private async Task SaveSettingsAsync()
    {
        _settings = new AppSettings
        {
            QuotaRefreshMinutes = QuotaRefreshMinutes,
            CloseToTray = CloseToTray,
            StartMinimized = StartMinimized
        };
        _quotaTimer.Interval = TimeSpan.FromMinutes(QuotaRefreshMinutes);

        if (!_isUiPreview)
        {
            try
            {
                await _settingsService.SaveAsync(
                    _settings,
                    _lifetimeCts.Token);
            }
            catch (OperationCanceledException) when (
                _lifetimeCts.IsCancellationRequested)
            {
                return;
            }
        }

        StatusMessage = _isUiPreview
            ? "预览设置已应用，本地文件未更改"
            : "设置已保存";
    }

    private void LoadUiPreviewAccounts()
    {
        var now = DateTimeOffset.Now;
        var reset = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            9,
            0,
            0,
            now.Offset).AddDays(4);
        var profiles = new[]
        {
            new AccountProfile
            {
                Nickname = "主账号",
                Email = "user@example.com",
                AccountId = "preview-primary",
                IsActive = true,
                MembershipPlan = MembershipPlan.Pro20x,
                Ownership = AccountOwnership.Personal,
                Quota = CreatePreviewQuota(
                    86,
                    72,
                    now,
                    now.AddHours(3),
                    reset,
                    QuotaStatus.Fresh)
            },
            new AccountProfile
            {
                Nickname = "工作账号",
                Email = "team@example.cn",
                AccountId = "preview-business",
                MembershipPlan = MembershipPlan.Business,
                Ownership = AccountOwnership.Organization(
                    "preview-organization",
                    "示例科技"),
                Quota = CreatePreviewQuota(
                    54,
                    41,
                    now.AddHours(-5),
                    now.AddHours(1),
                    reset,
                    QuotaStatus.Stale)
            },
            new AccountProfile
            {
                Nickname = "备用账号",
                Email = "backup@example.com",
                AccountId = "preview-backup",
                MembershipPlan = MembershipPlan.Plus,
                Ownership = AccountOwnership.Personal,
                Quota = CreatePreviewQuota(
                    100,
                    89,
                    now.AddHours(-15),
                    now.AddHours(4),
                    reset,
                    QuotaStatus.Fresh)
            }
        };

        Accounts.Clear();
        foreach (var profile in profiles)
        {
            Accounts.Add(new AccountCardViewModel(profile));
        }

        OnPropertyChanged(nameof(Accounts));
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static QuotaSnapshot CreatePreviewQuota(
        double fiveHourRemainingPercent,
        double weeklyRemainingPercent,
        DateTimeOffset fetchedAt,
        DateTimeOffset fiveHourResetsAt,
        DateTimeOffset weeklyResetsAt,
        QuotaStatus status) =>
        new()
        {
            FiveHourRemainingPercent = fiveHourRemainingPercent,
            FiveHourUsedPercent = 100 - fiveHourRemainingPercent,
            FiveHourWindowDurationMinutes = 300,
            FiveHourResetsAt = fiveHourResetsAt,
            RemainingPercent = weeklyRemainingPercent,
            UsedPercent = 100 - weeklyRemainingPercent,
            WindowDurationMinutes = 10_080,
            ResetsAt = weeklyResetsAt,
            FetchedAt = fetchedAt,
            Status = status
        };

    private async Task ReloadAccountsAsync(
        CancellationToken cancellationToken)
    {
        var profiles = await _vault.LoadProfilesAsync(cancellationToken);
        var existing = Accounts.ToDictionary(account => account.Id);
        Accounts.Clear();
        foreach (var profile in profiles
                     .OrderByDescending(item => item.IsActive)
                     .ThenBy(item => item.Nickname, StringComparer.CurrentCultureIgnoreCase))
        {
            if (existing.TryGetValue(profile.Id, out var account))
            {
                account.UpdateProfile(profile);
                Accounts.Add(account);
            }
            else
            {
                Accounts.Add(new AccountCardViewModel(profile));
            }
        }

        OnPropertyChanged(nameof(Accounts));
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DispatchStatus(string message)
    {
        if (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            StatusMessage = message;
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                if (!_lifetimeCts.IsCancellationRequested)
                {
                    StatusMessage = message;
                }
            });
        }
    }

    private async Task RunRefreshSessionAsync(
        Func<CancellationToken, Task> operation)
    {
        if (HasActiveRefresh)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
        _refreshCts = cancellation;
        Task task;
        try
        {
            task = operation(cancellation.Token);
        }
        catch
        {
            _refreshCts = null;
            cancellation.Dispose();
            throw;
        }

        _refreshTask = task;
        OnPropertyChanged(nameof(IsWorking));
        NotifyCommands();
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_refreshTask, task))
            {
                _refreshTask = Task.CompletedTask;
                _refreshCts = null;
                cancellation.Dispose();
                OnPropertyChanged(nameof(IsWorking));
                NotifyCommands();
            }
        }
    }

    private async Task CancelAndDrainRefreshesAsync()
    {
        var task = _refreshTask;
        if (task.IsCompleted)
        {
            return;
        }

        _refreshCts?.Cancel();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected handoff from refresh to mutation.
        }
    }

    private void StartPostSwitchRefresh(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null || _disposed || HasActiveRefresh)
        {
            return;
        }

        _ = RefreshAfterSwitchAsync(account);
    }

    private async Task RefreshAfterSwitchAsync(AccountCardViewModel account)
    {
        try
        {
            await RunRefreshSessionAsync(cancellationToken =>
                RefreshCardCoreAsync(
                    account,
                    QuotaRefreshReason.PostSwitch,
                    updateGlobalStatus: false,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // A later user action or shutdown supersedes the background refresh.
        }
        catch
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                StatusMessage = $"{account.Nickname} 已切换，额度将在稍后重试";
            }
        }
    }

    internal static string DescribeSwitchStage(
        SwitchStage stage,
        string nickname) =>
        stage switch
        {
            SwitchStage.ValidatingCredential => $"正在验证 {nickname} 的登录状态…",
            SwitchStage.StoppingChatGpt => "正在关闭 ChatGPT…",
            SwitchStage.CheckingBlockers => "正在检查共享认证进程…",
            SwitchStage.WritingCredential => "正在安全写入账号认证…",
            SwitchStage.LaunchingChatGpt => "正在启动 ChatGPT…",
            SwitchStage.Completed => $"已切换到 {nickname}",
            _ => $"正在切换到 {nickname}…"
        };

    private sealed class ImmediateProgress<T>(Action<T> report)
        : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private void NotifyCommands()
    {
        AddAccountCommand.NotifyCanExecuteChanged();
        ImportCurrentCommand.NotifyCanExecuteChanged();
        RefreshAllCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        SwitchAccountCommand.NotifyCanExecuteChanged();
        RefreshAccountCommand.NotifyCanExecuteChanged();
        RenameAccountCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _quotaTimer.Stop();
        _lifetimeCts.Cancel();
        _refreshCts?.Cancel();
    }
}
