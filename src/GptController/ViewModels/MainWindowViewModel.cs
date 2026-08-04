using System.Collections.ObjectModel;
using System.Windows.Threading;
using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Mvvm;
using GptController.Services;

namespace GptController.ViewModels;

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
    private readonly DeepSeekConnectionStore _deepSeekStore;
    private readonly DeepSeekCredentialStore _deepSeekCredentialStore;
    private readonly IDeepSeekApiClient _deepSeekApiClient;
    private readonly CodexVersionService _codexVersionService;
    private readonly ConnectionSwitchCoordinator _connectionSwitchCoordinator;
    private readonly ConnectionIndexStore _connectionIndexStore;
    private readonly string _credentialHelperPath;
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
        DeepSeekConnectionStore deepSeekStore,
        DeepSeekCredentialStore deepSeekCredentialStore,
        IDeepSeekApiClient deepSeekApiClient,
        CodexVersionService codexVersionService,
        ConnectionSwitchCoordinator connectionSwitchCoordinator,
        ConnectionIndexStore connectionIndexStore,
        string credentialHelperPath,
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
        _deepSeekStore = deepSeekStore;
        _deepSeekCredentialStore = deepSeekCredentialStore;
        _deepSeekApiClient = deepSeekApiClient;
        _codexVersionService = codexVersionService;
        _connectionSwitchCoordinator = connectionSwitchCoordinator;
        _connectionIndexStore = connectionIndexStore;
        _credentialHelperPath = credentialHelperPath;
        _isUiPreview = isUiPreview;

        AddAccountCommand = new AsyncRelayCommand(
            AddAccountAsync,
            () => !IsBusy && !HasPendingAccountRecovery);
        ImportCurrentCommand = new AsyncRelayCommand(
            ImportCurrentAsync,
            () => !IsBusy && !HasPendingAccountRecovery);
        ConfigureDeepSeekCommand = new AsyncRelayCommand(
            ConfigureDeepSeekAsync,
            () => !IsBusy && !HasActiveRefresh && !HasPendingAccountRecovery);
        RefreshAllCommand = new AsyncRelayCommand(
            RefreshAllAsync,
            () => !IsBusy && !HasActiveRefresh && !HasPendingAccountRecovery);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        ShowAccountsCommand = new RelayCommand(ShowAccountsPage);
        ShowSettingsCommand = new RelayCommand(ShowSettingsPage);
        SwitchAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            SwitchAccountAsync,
            account => !IsBusy && !HasPendingAccountRecovery && !account.IsActive);
        RefreshAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            RefreshAccountAsync,
            account =>
                !IsBusy &&
                !HasActiveRefresh &&
                !HasPendingAccountRecovery &&
                !account.IsRefreshing);
        RenameAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            RenameAccountAsync,
            _ => !IsBusy && !HasPendingAccountRecovery);
        DeleteAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            DeleteAccountAsync,
            account => !IsBusy && !HasPendingAccountRecovery && account.CanDelete);
        TestApiConnectionCommand = new AsyncRelayCommand<AccountCardViewModel>(
            TestDeepSeekAsync,
            account =>
                !IsBusy &&
                !HasActiveRefresh &&
                !HasPendingAccountRecovery &&
                account.IsDeepSeek);

        _quotaTimer.Tick += async (_, _) =>
        {
            if (!IsBusy && !HasActiveRefresh && !HasPendingAccountRecovery)
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
    public AsyncRelayCommand ConfigureDeepSeekCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand ShowAccountsCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> SwitchAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> RefreshAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> RenameAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> DeleteAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> TestApiConnectionCommand { get; }

    public event EventHandler? AccountsChanged;

    public string ApplicationVersion => ApplicationInfo.Version;

    public string DeepSeekMenuLabel => Accounts.Any(item => item.IsDeepSeek)
        ? "编辑 DeepSeek API"
        : "添加 DeepSeek API";

    public string CurrentProviderText => Accounts.FirstOrDefault(item => item.IsActive) is { } account
        ? $"当前连接 · {account.ProviderDisplayName} · {account.Nickname}"
        : "当前连接 · 尚未选择";

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

    private bool HasPendingAccountRecovery =>
        _switchCoordinator.HasPendingTransaction;

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
            StatusMessage = "界面预览模式 · 连接操作不会写入本地数据";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "正在检查恢复状态…";
            var recovered = await _switchCoordinator.RecoverPendingTransactionAsync(
                _lifetimeCts.Token);
            if (HasPendingAccountRecovery)
            {
                await ReloadAccountsAsync(_lifetimeCts.Token);
                StatusMessage = "账号恢复未完成，请关闭 ChatGPT 后重启本应用";
                _dialogs.Error(
                    "账号恢复未完成",
                    "为了保护现有登录状态，连接操作已暂时锁定。请完全关闭 ChatGPT，然后重新启动本应用以重试恢复。");
                return;
            }

            recovered |= await _connectionSwitchCoordinator.RecoverProviderStateAsync(
                _lifetimeCts.Token);
            if (recovered)
            {
                StatusMessage = "已恢复上次未完成的账号切换";
            }

            await ReloadAccountsAsync(_lifetimeCts.Token);
            if (Accounts.All(item => item.IsDeepSeek) &&
                _importService.HasLiveAccount &&
                _dialogs.Ask(
                    "导入当前账号",
                    "检测到 ChatGPT 当前登录账号。是否将它安全导入账号管理器？",
                    yesActionText: "导入",
                    noActionText: "稍后"))
            {
                await ImportCurrentCoreAsync(_lifetimeCts.Token);
            }

            if (!recovered)
            {
                StatusMessage = Accounts.Count > 0
                    ? "已加载连接数据"
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
            if (!_disposed && !HasPendingAccountRecovery)
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

    private async Task ConfigureDeepSeekAsync()
    {
        var existingCard = Accounts.FirstOrDefault(item => item.IsDeepSeek);
        var input = _dialogs.PromptDeepSeekConnection(
            existingCard?.Nickname ?? "DeepSeek V4",
            existingCard is not null);
        if (input is null)
        {
            return;
        }

        if (_isUiPreview)
        {
            var preview = CreatePreviewDeepSeek() with { Nickname = input.Nickname };
            if (existingCard is null)
            {
                Accounts.Insert(0, new AccountCardViewModel(preview));
            }
            else
            {
                existingCard.UpdateDeepSeek(preview);
            }

            NotifyConnectionsChanged();
            StatusMessage = "预览：DeepSeek 连接已更新";
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            if (!File.Exists(_credentialHelperPath))
            {
                throw new FileNotFoundException(
                    "DeepSeek 凭据助手缺失，请使用完整的 GPT Controller 应用包。",
                    _credentialHelperPath);
            }

            StatusMessage = "正在检查 Codex 版本…";
            var version = await _codexVersionService.CheckAsync(_lifetimeCts.Token);
            if (!version.IsSupported)
            {
                var installed = version.InstalledVersion?.ToString() ?? version.DisplayText;
                throw new InvalidOperationException(
                    $"DeepSeek 连接要求 Codex {version.MinimumVersion} 或更高版本，当前为 {installed}。");
            }

            var apiKey = input.ApiKey ??
                await _deepSeekCredentialStore.ReadAsync(_lifetimeCts.Token);
            StatusMessage = "正在验证 DeepSeek API Key 与余额…";
            var balance = await _deepSeekApiClient.GetBalanceAsync(
                apiKey,
                _lifetimeCts.Token);
            var now = DateTimeOffset.UtcNow;
            var connection = (existingCard?.DeepSeekProfile ?? new DeepSeekConnection()) with
            {
                Nickname = input.Nickname,
                LastValidatedAt = now,
                IsAvailable = balance.IsAvailable,
                Status = balance.IsAvailable
                    ? DeepSeekConnectionStatus.Available
                    : DeepSeekConnectionStatus.Unavailable,
                ErrorCode = null,
                Balance = balance,
                CnyBalance = ReadBalance(balance, "CNY"),
                UsdBalance = ReadBalance(balance, "USD")
            };
            await _deepSeekStore.SaveAsync(
                connection,
                input.ApiKey,
                _lifetimeCts.Token);
            await ReloadAccountsAsync(_lifetimeCts.Token);
            StatusMessage = "DeepSeek API 连接已验证并安全保存";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels configuration.
        }
        catch (DeepSeekApiException exception)
        {
            StatusMessage = "DeepSeek API 验证失败";
            _dialogs.Error("DeepSeek 连接", exception.Message);
        }
        catch (Exception exception)
        {
            StatusMessage = "DeepSeek 连接保存失败";
            _dialogs.Error("DeepSeek 连接", RedactingLogger.Redact(exception.Message));
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
                        StatusMessage = "正在刷新所有连接数据…";
                    }

                    foreach (var account in Accounts.ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (reason == QuotaRefreshReason.Automatic &&
                            !account.IsDeepSeek &&
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
                        : "连接数据已更新";
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
            StatusMessage = "部分连接刷新失败";
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
            StatusMessage = "连接刷新失败";
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

            if (account.IsDeepSeek)
            {
                await RefreshDeepSeekCoreAsync(account, cancellationToken);
            }
            else
            {
                var updated = await _quotaService.RefreshAsync(
                    account.Id,
                    reason,
                    cancellationToken);
                account.UpdateProfile(updated);
            }
            NotifyConnectionsChanged();
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

    private async Task RefreshDeepSeekCoreAsync(
        AccountCardViewModel account,
        CancellationToken cancellationToken)
    {
        var current = account.DeepSeekProfile
            ?? throw new InvalidOperationException("DeepSeek 连接不存在。");
        try
        {
            var apiKey = await _deepSeekCredentialStore.ReadAsync(cancellationToken);
            var balance = await _deepSeekApiClient.GetBalanceAsync(apiKey, cancellationToken);
            var updated = current with
            {
                LastValidatedAt = DateTimeOffset.UtcNow,
                IsAvailable = balance.IsAvailable,
                Status = balance.IsAvailable
                    ? DeepSeekConnectionStatus.Available
                    : DeepSeekConnectionStatus.Unavailable,
                ErrorCode = null,
                Balance = balance,
                CnyBalance = ReadBalance(balance, "CNY"),
                UsdBalance = ReadBalance(balance, "USD")
            };
            updated = await _deepSeekStore.SaveAsync(
                updated,
                cancellationToken: cancellationToken);
            account.UpdateDeepSeek(updated);
        }
        catch (DeepSeekApiException exception)
        {
            var failed = current with
            {
                Status = MapConnectionStatus(exception.ErrorKind),
                ErrorCode = exception.ErrorKind.ToString(),
                IsAvailable = false
            };
            failed = await _deepSeekStore.SaveAsync(
                failed,
                cancellationToken: cancellationToken);
            account.UpdateDeepSeek(failed);
            throw;
        }
    }

    private async Task SwitchAccountAsync(AccountCardViewModel account)
    {
        if (account.IsActive)
        {
            StatusMessage = $"{account.Nickname} 已经是当前连接";
            return;
        }

        if (_isUiPreview)
        {
            if (!_dialogs.Confirm(
                    "切换连接并重启 ChatGPT",
                    $"切换到“{account.Nickname}”需要关闭并重启 ChatGPT。\n\n" +
                    "正在运行的任务可能会被中断。不同认证组的历史记录只会暂时隐藏，不会被删除。",
                    primaryActionText: "切换并重启"))
            {
                StatusMessage = "预览：已取消连接切换";
                return;
            }

            var cards = Accounts.Select(item => item.IsDeepSeek
                    ? new AccountCardViewModel(item.DeepSeekProfile! with
                    {
                        IsActive = item.Id == account.Id
                    })
                    : new AccountCardViewModel(item.Profile with
                    {
                        IsActive = item.Id == account.Id
                    }))
                .OrderByDescending(item => item.IsActive)
                .ThenBy(item => item.Nickname, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Accounts.Clear();
            foreach (var card in cards)
            {
                Accounts.Add(card);
            }

            NotifyConnectionsChanged();
            StatusMessage = $"预览：已切换到 {account.Nickname}";
            return;
        }

        if (_processController.IsChatGptRunning() &&
            !_dialogs.Confirm(
                "切换连接并重启 ChatGPT",
                $"切换到“{account.Nickname}”需要关闭并重启 ChatGPT。\n\n" +
                "正在运行的任务可能会被中断。不同认证组的历史记录只会暂时隐藏，不会被删除。\n\n" +
                "默认操作是取消；确认继续吗？",
                primaryActionText: "切换并重启"))
        {
            StatusMessage = "已取消连接切换";
            return;
        }

        IsBusy = true;
        var refreshAfterSwitch = false;
        try
        {
            await CancelAndDrainRefreshesAsync();
            if (account.IsDeepSeek)
            {
                StatusMessage = "正在检查 Codex 版本…";
                var version = await _codexVersionService.CheckAsync(
                    _lifetimeCts.Token);
                if (!version.IsSupported)
                {
                    var installed = version.InstalledVersion?.ToString() ??
                        version.DisplayText;
                    StatusMessage = "Codex 版本不支持 DeepSeek";
                    _dialogs.Error(
                        "无法启用 DeepSeek",
                        $"DeepSeek 连接要求 Codex {version.MinimumVersion} 或更高版本，当前为 {installed}。");
                    return;
                }
            }

            var progress = new ImmediateProgress<SwitchStage>(stage =>
                StatusMessage = DescribeSwitchStage(stage, account.Nickname));
            var result = account.IsDeepSeek
                ? await _connectionSwitchCoordinator.SwitchToDeepSeekAsync(
                    progress,
                    _lifetimeCts.Token)
                : await _connectionSwitchCoordinator.SwitchToChatGptAsync(
                    account.Id,
                    forceConfigRestore: false,
                    progress,
                    _lifetimeCts.Token);
            if (result.Status == SwitchStatus.ConfigurationConflict &&
                !account.IsDeepSeek &&
                _dialogs.Confirm(
                    "Codex 配置冲突",
                    result.Message +
                    "\n\n按备份恢复只会覆盖 DeepSeek 接管的字段；新增的 MCP、项目信任和其他 Provider 会保留。确认继续吗？",
                    primaryActionText: "按备份恢复"))
            {
                result = await _connectionSwitchCoordinator.SwitchToChatGptAsync(
                    account.Id,
                    forceConfigRestore: true,
                    progress,
                    _lifetimeCts.Token);
            }
            await ReloadAccountsAsync(_lifetimeCts.Token);
            StatusMessage = result.Message;
            if (!result.IsSuccess)
            {
                _dialogs.Error("连接切换", result.Message);
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
            StatusMessage = "连接切换失败";
            _dialogs.Error("连接切换失败", RedactingLogger.Redact(exception.Message));
        }
        finally
        {
            IsBusy = false;
        }

        if (refreshAfterSwitch && !_disposed && !account.IsDeepSeek)
        {
            StartPostSwitchRefresh(account.Id);
        }
    }

    private async Task RenameAccountAsync(AccountCardViewModel account)
    {
        if (account.IsDeepSeek)
        {
            await ConfigureDeepSeekAsync();
            return;
        }

        if (_isUiPreview)
        {
            var previewNickname = _dialogs.Prompt(
                "编辑昵称",
                "账号昵称",
                account.Nickname);
            if (string.IsNullOrWhiteSpace(previewNickname) ||
                previewNickname == account.Nickname)
            {
                return;
            }

            account.UpdateProfile(account.Profile with
            {
                Nickname = previewNickname.Trim()
            });
            NotifyConnectionsChanged();
            StatusMessage = "预览：昵称已更新（未写入本地数据）";
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
        catch (Exception exception)
        {
            StatusMessage = "昵称更新失败";
            _dialogs.Error(
                "编辑昵称失败",
                RedactingLogger.Redact(exception.Message));
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
            if (_dialogs.Confirm(
                    "删除连接",
                    $"确定删除“{account.Nickname}”吗？\n\n预览模式只演示确认流程，不会修改本地数据。",
                    primaryActionText: "删除",
                    isDangerous: true))
            {
                StatusMessage = $"预览：已模拟删除 {account.Nickname}（数据未更改）";
            }

            return;
        }

        if (account.IsActive)
        {
            _dialogs.Info("无法删除", "请先切换到其他连接，再删除当前连接。");
            return;
        }

        if (!_dialogs.Confirm(
                "删除连接",
                account.IsDeepSeek
                    ? $"确定删除“{account.Nickname}”吗？\n\n本机 DPAPI 加密的 API Key 与连接元数据会一并删除。"
                    : $"确定删除“{account.Nickname}”吗？\n\n只会删除本软件保存的加密档案，不会退出或注销 OpenAI 账号。",
                primaryActionText: "删除",
                isDangerous: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            if (account.IsDeepSeek)
            {
                await _deepSeekStore.DeleteAsync(_lifetimeCts.Token);
            }
            else
            {
                await _vault.DeleteProfileAsync(account.Id, _lifetimeCts.Token);
            }
            await ReloadAccountsAsync(_lifetimeCts.Token);
            StatusMessage = "连接已删除";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown cancels mutation without showing an error.
        }
        catch (Exception exception)
        {
            if (account.IsDeepSeek)
            {
                try
                {
                    await ReloadAccountsAsync(CancellationToken.None);
                }
                catch
                {
                    // Keep the original deletion error. A later reload or restart
                    // will reconcile any partially deleted connection metadata.
                }
            }

            StatusMessage = "连接删除失败";
            _dialogs.Error(
                "删除失败",
                RedactingLogger.Redact(exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestDeepSeekAsync(AccountCardViewModel account)
    {
        if (!account.IsDeepSeek)
        {
            return;
        }

        if (_isUiPreview)
        {
            if (!_dialogs.Confirm(
                    "测试 DeepSeek Responses",
                    "将发送一个要求仅回复 OK 的最小 Responses API 请求，会产生少量 Token 费用。",
                    primaryActionText: "发送测试请求"))
            {
                return;
            }

            StatusMessage = "预览：DeepSeek Responses 测试成功";
            _dialogs.Info(
                "Responses 测试成功",
                "模型返回：OK\n响应 ID：resp_preview_120\nToken：3");
            return;
        }

        if (!_dialogs.Confirm(
                "测试 DeepSeek Responses",
                "将发送一个要求仅回复 OK 的最小 Responses API 请求，会产生少量 Token 费用。确认继续吗？",
                primaryActionText: "发送测试请求"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await CancelAndDrainRefreshesAsync();
            StatusMessage = "正在测试 DeepSeek Responses API…";
            var apiKey = await _deepSeekCredentialStore.ReadAsync(_lifetimeCts.Token);
            var result = await _deepSeekApiClient.TestResponseAsync(
                apiKey,
                _lifetimeCts.Token);
            var profile = account.DeepSeekProfile! with
            {
                LastValidatedAt = DateTimeOffset.UtcNow,
                IsAvailable = true,
                Status = DeepSeekConnectionStatus.Available,
                ErrorCode = null
            };
            profile = await _deepSeekStore.SaveAsync(
                profile,
                cancellationToken: _lifetimeCts.Token);
            account.UpdateDeepSeek(profile);
            NotifyConnectionsChanged();
            StatusMessage = "DeepSeek Responses API 测试成功";
            _dialogs.Info(
                "Responses 测试成功",
                $"模型返回：{result.OutputText.Trim()}\n响应 ID：{result.ResponseId}\nToken：{result.TotalTokens?.ToString() ?? "未返回"}");
        }
        catch (DeepSeekApiException exception)
        {
            StatusMessage = "DeepSeek Responses API 测试失败";
            _dialogs.Error("Responses 测试失败", exception.Message);
        }
        catch (Exception exception)
        {
            StatusMessage = "DeepSeek Responses API 测试失败";
            _dialogs.Error("Responses 测试失败", RedactingLogger.Redact(exception.Message));
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
                "是否备份 config.toml 并切换到 file 模式？",
                primaryActionText: "备份并切换"))
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
        Accounts.Add(new AccountCardViewModel(CreatePreviewDeepSeek()));
        foreach (var profile in profiles)
        {
            Accounts.Add(new AccountCardViewModel(profile));
        }

        NotifyConnectionsChanged();
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

    private static decimal? ReadBalance(
        DeepSeekBalanceSnapshot balance,
        string currency) =>
        balance.Balances.FirstOrDefault(item => string.Equals(
            item.Currency,
            currency,
            StringComparison.OrdinalIgnoreCase))?.TotalBalance;

    private static DeepSeekConnectionStatus MapConnectionStatus(
        DeepSeekApiErrorKind kind) => kind switch
        {
            DeepSeekApiErrorKind.AuthenticationRequired =>
                DeepSeekConnectionStatus.AuthenticationRequired,
            DeepSeekApiErrorKind.PaymentRequired =>
                DeepSeekConnectionStatus.PaymentRequired,
            DeepSeekApiErrorKind.RateLimited =>
                DeepSeekConnectionStatus.RateLimited,
            DeepSeekApiErrorKind.Timeout or DeepSeekApiErrorKind.Network =>
                DeepSeekConnectionStatus.Stale,
            _ => DeepSeekConnectionStatus.Unavailable
        };

    private async Task ReloadAccountsAsync(
        CancellationToken cancellationToken)
    {
        var profiles = await _vault.LoadProfilesAsync(cancellationToken);
        var deepSeek = await _deepSeekStore.GetAsync(cancellationToken);
        await _connectionIndexStore.SaveProjectionAsync(
            profiles,
            deepSeek,
            cancellationToken);
        var existing = Accounts.ToDictionary(account => account.Id);
        Accounts.Clear();
        if (deepSeek is not null)
        {
            if (existing.TryGetValue(AccountCardViewModel.DeepSeekCardId, out var deepSeekCard) &&
                deepSeekCard.IsDeepSeek)
            {
                deepSeekCard.UpdateDeepSeek(deepSeek);
                Accounts.Add(deepSeekCard);
            }
            else
            {
                Accounts.Add(new AccountCardViewModel(deepSeek));
            }
        }

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

        var ordered = Accounts
            .OrderByDescending(item => item.IsActive)
            .ThenBy(item => item.Provider)
            .ThenBy(item => item.Nickname, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Accounts.Clear();
        foreach (var item in ordered)
        {
            Accounts.Add(item);
        }

        NotifyConnectionsChanged();
    }

    private static DeepSeekConnection CreatePreviewDeepSeek() => new()
    {
        Nickname = "DeepSeek V4",
        KeyLastFour = "8K2Q",
        IsAvailable = true,
        Status = DeepSeekConnectionStatus.Available,
        CnyBalance = 42.80m,
        UsdBalance = 6.25m,
        LastValidatedAt = DateTimeOffset.Now.AddMinutes(-3)
    };

    private void NotifyConnectionsChanged()
    {
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(DeepSeekMenuLabel));
        OnPropertyChanged(nameof(CurrentProviderText));
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
            SwitchStage.ConfiguringProvider => "正在安全更新 Codex Responses 配置…",
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
        ConfigureDeepSeekCommand.NotifyCanExecuteChanged();
        RefreshAllCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        SwitchAccountCommand.NotifyCanExecuteChanged();
        RefreshAccountCommand.NotifyCanExecuteChanged();
        RenameAccountCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        TestApiConnectionCommand.NotifyCanExecuteChanged();
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
