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
    private readonly DispatcherTimer _quotaTimer = new();
    private AppSettings _settings = new();
    private bool _isBusy;
    private string _statusMessage = "准备就绪";
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
        DialogService dialogs)
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

        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync, () => !IsBusy);
        ImportCurrentCommand = new AsyncRelayCommand(ImportCurrentAsync, () => !IsBusy);
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsBusy);
        SwitchAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            SwitchAccountAsync,
            _ => !IsBusy);
        RefreshAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            RefreshAccountAsync,
            _ => !IsBusy);
        RenameAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            RenameAccountAsync,
            _ => !IsBusy);
        DeleteAccountCommand = new AsyncRelayCommand<AccountCardViewModel>(
            DeleteAccountAsync,
            account => !IsBusy && account.CanDelete);

        _quotaTimer.Tick += async (_, _) =>
        {
            if (!IsBusy)
            {
                await RefreshAllAsync(silent: true);
            }
        };
    }

    public ObservableCollection<AccountCardViewModel> Accounts { get; } = [];

    public AsyncRelayCommand AddAccountCommand { get; }
    public AsyncRelayCommand ImportCurrentCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> SwitchAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> RefreshAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> RenameAccountCommand { get; }
    public AsyncRelayCommand<AccountCardViewModel> DeleteAccountCommand { get; }

    public event EventHandler? AccountsChanged;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync();
        _quotaTimer.Interval = TimeSpan.FromMinutes(
            Math.Clamp(_settings.QuotaRefreshMinutes, 5, 120));

        IsBusy = true;
        try
        {
            StatusMessage = "正在检查恢复状态…";
            var recovered = await _switchCoordinator.RecoverPendingTransactionAsync();
            if (recovered)
            {
                StatusMessage = "已恢复上次未完成的账号切换";
            }

            await ReloadAccountsAsync();
            if (Accounts.Count == 0 &&
                _importService.HasLiveAccount &&
                _dialogs.Ask(
                    "导入当前账号",
                    "检测到 ChatGPT 当前登录账号。是否将它安全导入账号管理器？"))
            {
                await ImportCurrentCoreAsync();
            }

            if (Accounts.Count > 0)
            {
                await RefreshAllAsync(silent: true);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = "初始化失败";
            _dialogs.Error("初始化失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
            _quotaTimer.Start();
        }
    }

    public async Task SwitchFromTrayAsync(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is not null)
        {
            await SwitchAccountAsync(account);
        }
    }

    private async Task AddAccountAsync()
    {
        if (!await EnsureFileStoreAsync())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var account = await _oauthService.AddAccountAsync(
                message => DispatchStatus(message));
            StatusMessage = $"已添加 {account.Nickname}";
            await ReloadAccountsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消添加账号";
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
        if (!await EnsureFileStoreAsync())
        {
            return;
        }

        IsBusy = true;
        try
        {
            await ImportCurrentCoreAsync();
            await RefreshAllAsync(silent: true);
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

    private async Task ImportCurrentCoreAsync()
    {
        StatusMessage = "正在导入当前账号…";
        var imported = await _importService.ImportAsync();
        StatusMessage = $"已导入 {imported.Nickname}";
        await ReloadAccountsAsync();
    }

    private Task RefreshAllAsync() => RefreshAllAsync(silent: false);

    private async Task RefreshAllAsync(bool silent)
    {
        if (Accounts.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!silent)
            {
                StatusMessage = "正在刷新所有账号额度…";
            }

            await _quotaService.RefreshAllAsync();
            await ReloadAccountsAsync();
            StatusMessage = "额度已更新";
        }
        catch (Exception exception)
        {
            StatusMessage = "部分额度刷新失败";
            if (!silent)
            {
                _dialogs.Error("刷新失败", exception.Message);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAccountAsync(AccountCardViewModel account)
    {
        IsBusy = true;
        try
        {
            StatusMessage = $"正在刷新 {account.Nickname}…";
            await _quotaService.RefreshAsync(account.Id);
            await ReloadAccountsAsync();
            StatusMessage = $"{account.Nickname} 已更新";
        }
        catch (Exception exception)
        {
            StatusMessage = "额度刷新失败";
            _dialogs.Error("刷新失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SwitchAccountAsync(AccountCardViewModel account)
    {
        if (account.IsActive)
        {
            StatusMessage = $"{account.Nickname} 已经是当前账号";
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
        try
        {
            StatusMessage = $"正在切换到 {account.Nickname}…";
            var result = await _switchCoordinator.SwitchAsync(account.Id);
            await ReloadAccountsAsync();
            StatusMessage = result.Message;
            if (!result.IsSuccess)
            {
                _dialogs.Error("账号切换", result.Message);
                return;
            }

            await _quotaService.RefreshAsync(account.Id);
            await ReloadAccountsAsync();
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
    }

    private async Task RenameAccountAsync(AccountCardViewModel account)
    {
        var nickname = _dialogs.Prompt("编辑昵称", "账号昵称", account.Nickname);
        if (string.IsNullOrWhiteSpace(nickname) || nickname == account.Nickname)
        {
            return;
        }

        var profile = await _vault.GetProfileAsync(account.Id);
        if (profile is null)
        {
            return;
        }

        await _vault.UpsertProfileAsync(profile with { Nickname = nickname.Trim() });
        await ReloadAccountsAsync();
        StatusMessage = "昵称已更新";
    }

    private async Task DeleteAccountAsync(AccountCardViewModel account)
    {
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

        await _vault.DeleteProfileAsync(account.Id);
        await ReloadAccountsAsync();
        StatusMessage = "账号档案已删除";
    }

    private async Task<bool> EnsureFileStoreAsync()
    {
        if (await _configService.IsFileStoreCompatibleAsync())
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

        await _configService.EnableFileStoreAsync();
        _dialogs.Info("配置已更新", "已切换到文件认证模式。重新登录后即可管理账号。");
        return true;
    }

    private async Task ReloadAccountsAsync()
    {
        var profiles = await _vault.LoadProfilesAsync();
        Accounts.Clear();
        foreach (var profile in profiles
                     .OrderByDescending(item => item.IsActive)
                     .ThenBy(item => item.Nickname, StringComparer.CurrentCultureIgnoreCase))
        {
            Accounts.Add(new AccountCardViewModel(profile));
        }

        OnPropertyChanged(nameof(Accounts));
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DispatchStatus(string message)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            StatusMessage = message;
        }
        else
        {
            dispatcher.Invoke(() => StatusMessage = message);
        }
    }

    private void NotifyCommands()
    {
        AddAccountCommand.NotifyCanExecuteChanged();
        ImportCurrentCommand.NotifyCanExecuteChanged();
        RefreshAllCommand.NotifyCanExecuteChanged();
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
    }
}
