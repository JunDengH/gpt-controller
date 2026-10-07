using System.Net.Http;
using System.Windows;
using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Services;
using GptController.ViewModels;
using GptController.Views;
using Application = System.Windows.Application;

namespace GptController;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MainWindowViewModel? _viewModel;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private HttpClient? _deepSeekHttpClient;
    private HttpClient? _qwenHttpClient;
    private HttpClient? _accountDetailsHttpClient;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UninstallCodexConfigRestoreRunner.IsRequested(e.Args))
        {
            var exitCode = await UninstallCodexConfigRestoreRunner.RunDefaultAsync();
            Shutdown((int)exitCode);
            return;
        }

        var isCompactUiPreview = e.Args.Any(
            argument => string.Equals(
                argument,
                "--ui-preview-compact",
                StringComparison.OrdinalIgnoreCase));
        var isUiPreview = isCompactUiPreview || e.Args.Any(
            argument => string.Equals(
                argument,
                "--ui-preview",
                StringComparison.OrdinalIgnoreCase));
#if DEBUG
        isUiPreview = isUiPreview ||
                      File.Exists(Path.Combine(AppContext.BaseDirectory, ".ui-preview"));
#endif
        var applicationMutexName = isUiPreview
            ? $"{LegacyCompatibility.ApplicationMutexName}.UiPreview.{Environment.ProcessId}"
            : LegacyCompatibility.ApplicationMutexName;
        _singleInstance = new Mutex(
            initiallyOwned: true,
            applicationMutexName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            new DialogService().Info(
                "GPT Controller",
                "GPT Controller 已经在运行。");
            Shutdown();
            return;
        }

        try
        {
            var credentialHelperPath = Path.Combine(
                AppContext.BaseDirectory,
                CredentialHelperLocator.ExecutableName);
            var paths = isUiPreview
                ? new AppPaths(
                    Path.Combine(Path.GetTempPath(), "GptController.UiPreview"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    $"process-{Environment.ProcessId}")
                : new AppPaths();
            if (!isUiPreview)
            {
                var migrator = new ApplicationDataMigrator(paths);
                await migrator.MigrateIfNeededAsync(
                    (stagingPaths, cancellationToken) =>
                        RecoverStagedMigrationStateAsync(
                            stagingPaths,
                            credentialHelperPath,
                            cancellationToken));
            }

            paths.EnsureCreated();
            CleanupTemp(paths.Temp);

            var logger = new RedactingLogger(paths);
            var vault = new ProfileVault(paths);
            var settingsService = new SettingsService(paths);
            var configService = new CodexConfigService(paths);
            _accountDetailsHttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            { Timeout = TimeSpan.FromSeconds(8) };
            var metadataService = new AccountMetadataService(new ChatGptAccountDetailsClient(_accountDetailsHttpClient));
            var quotaParser = new QuotaParser();
            var locator = new CodexLocator(paths);
            var appServerFactory = new CodexAppServerClientFactory();
            var processController = new ChatGptProcessController(locator, logger);
            var operationGate = new OperationGate();
            var quotaService = new QuotaService(
                paths,
                vault,
                locator,
                appServerFactory,
                processController,
                metadataService,
                quotaParser,
                operationGate,
                logger);
            var oauthService = new OAuthAccountService(
                paths,
                vault,
                locator,
                appServerFactory,
                metadataService,
                quotaParser,
                operationGate,
                logger);
            var importService = new CurrentAccountImportService(
                paths,
                vault,
                metadataService);
            var switchCoordinator = new SwitchCoordinator(
                paths,
                vault,
                processController,
                operationGate,
                logger);
            var deepSeekCredentialStore = new DeepSeekCredentialStore(paths.Root);
            var deepSeekStore = new DeepSeekConnectionStore(
                paths,
                deepSeekCredentialStore);
            var qwenCredentialStore = new QwenCredentialStore(paths.Root);
            var qwenStore = new QwenConnectionStore(paths, qwenCredentialStore);
            var deepSeekConfigService = new DeepSeekCodexConfigService(
                new DeepSeekCodexConfigOptions(
                    paths.CodexConfigFile,
                    paths.DeepSeekModelCatalogFile,
                    paths.DeepSeekConfigStateFile,
                    credentialHelperPath));
            _deepSeekHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var deepSeekApiClient = new DeepSeekApiClient(_deepSeekHttpClient);
            _qwenHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var qwenApiClient = new QwenApiClient(_qwenHttpClient);
            var codexVersionService = new CodexVersionService(locator);
            var connectionSwitchCoordinator = new ConnectionSwitchCoordinator(
                vault,
                deepSeekStore,
                deepSeekCredentialStore,
                deepSeekConfigService,
                switchCoordinator,
                processController,
                operationGate,
                logger,
                qwenStore: qwenStore,
                qwenCredentialStore: qwenCredentialStore);
            var connectionIndexStore = new ConnectionIndexStore(paths);
            var dialogs = new DialogService();

            _viewModel = new MainWindowViewModel(
                vault,
                settingsService,
                configService,
                importService,
                oauthService,
                quotaService,
                switchCoordinator,
                processController,
                dialogs,
                deepSeekStore,
                deepSeekCredentialStore,
                deepSeekApiClient,
                codexVersionService,
                connectionSwitchCoordinator,
                connectionIndexStore,
                credentialHelperPath,
                isUiPreview,
                qwenStore,
                qwenCredentialStore,
                qwenApiClient);
            _mainWindow = new MainWindow(_viewModel);
            if (isCompactUiPreview)
            {
                _mainWindow.Width = 960;
                _mainWindow.Height = 680;
            }
            if (isUiPreview && e.Args.Contains("--reference-preview"))
            {
                _mainWindow.Width = 1490;
                _mainWindow.Height = 1056;
            }

            MainWindow = _mainWindow;
            var renderIndex = Array.IndexOf(e.Args, "--render-preview");
            if (isUiPreview && renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
            {
                await _viewModel.InitializeAsync();
                if (e.Args.Contains("--empty-preview"))
                {
                    _viewModel.Accounts.Clear();
                    _viewModel.NotifyConnectionsChanged();
                }
                if (e.Args.Contains("--settings-preview"))
                {
                    _viewModel.ShowSettingsCommand.Execute(null);
                }
                var size = new Size(_mainWindow.Width, _mainWindow.Height);
                _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _mainWindow.Left = -10000;
                _mainWindow.Top = -10000;
                _mainWindow.ShowActivated = false;
                _mainWindow.Show();
                _mainWindow.Measure(size);
                _mainWindow.Arrange(new Rect(size));
                _mainWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (e.Args.Contains("--exercise-ledger"))
                {
                    var ledger = (System.Windows.Controls.ListBox)_mainWindow.FindName("ConnectionLedger");
                    var title = (System.Windows.Controls.TextBlock)_mainWindow.FindName("InspectorTitle");
                    var original = _viewModel.SelectedConnection;
                    foreach (var connection in _viewModel.Accounts.ToArray())
                    {
                        ledger.SelectedItem = connection;
                        _mainWindow.UpdateLayout();
                        if (_viewModel.SelectedConnection != connection || title.Text != connection.LedgerName)
                            throw new InvalidOperationException("The ledger selection did not update the inspector.");
                    }
                    ledger.SelectedItem = original;
                    _viewModel.ShowSettingsCommand.Execute(null);
                    _mainWindow.UpdateLayout();
                    if (((FrameworkElement)_mainWindow.FindName("SettingsPage")).Visibility != Visibility.Visible)
                        throw new InvalidOperationException("The settings navigation did not update the page.");
                    _viewModel.ShowAccountsCommand.Execute(null);
                    _mainWindow.UpdateLayout();
                    await File.WriteAllTextAsync(e.Args[renderIndex + 1] + ".interaction.txt",
                        $"Passed {_viewModel.Accounts.Count} ledger selections and settings navigation.\n");
                }
                System.Windows.Media.Visual previewVisual = (System.Windows.Media.Visual)_mainWindow.Content;
                if (e.Args.Contains("--models-preview"))
                {
                    var connection = _viewModel.Accounts.First(account => account.IsDeepSeek).DeepSeekProfile!;
                    var modelDialog = new ApiModelSelectionDialog("DeepSeek API", connection.Models, connection.Model, true);
                    modelDialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    modelDialog.Left = -10000;
                    modelDialog.Top = -10000;
                    modelDialog.ShowActivated = false;
                    modelDialog.Show();
                    modelDialog.UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    size = new Size(modelDialog.ActualWidth, modelDialog.ActualHeight);
                    previewVisual = (System.Windows.Media.Visual)modelDialog.Content;
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(previewVisual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.GetFullPath(e.Args[renderIndex + 1])))
                {
                    encoder.Save(output);
                }
                ExitApplication();
                return;
            }
            _trayIcon = new TrayIconService(
                ShowMainWindow,
                ExitApplication,
                connection => _viewModel.SwitchAccountCommand.Execute(connection));
            _viewModel.AccountsChanged += (_, _) =>
                _trayIcon.UpdateAccounts(_viewModel.Accounts.ToList());

            _mainWindow.Show();
            await _viewModel.InitializeAsync();
            if (!isUiPreview && _viewModel.StartMinimized)
            {
                _mainWindow.Hide();
            }
        }
        catch (Exception exception)
        {
            var renderIndex = Array.IndexOf(e.Args, "--render-preview");
            if (isUiPreview && renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
            {
                await File.WriteAllTextAsync(e.Args[renderIndex + 1] + ".error.txt", exception.ToString());
                IsExiting = true;
                Shutdown(1);
                return;
            }
            new DialogService().Error(
                "GPT Controller 启动失败",
                exception.Message);
            ExitApplication();
        }
    }

    private static async Task RecoverStagedMigrationStateAsync(
        AppPaths paths,
        string credentialHelperPath,
        CancellationToken cancellationToken)
    {
        var logger = new RedactingLogger(paths);
        var vault = new ProfileVault(paths);
        var locator = new CodexLocator(paths);
        var processController = new ChatGptProcessController(locator, logger);
        var operationGate = new OperationGate();
        var accountSwitchCoordinator = new SwitchCoordinator(
            paths,
            vault,
            processController,
            operationGate,
            logger);
        if (File.Exists(paths.TransactionFile))
        {
            var recovered = await accountSwitchCoordinator.RecoverPendingTransactionAsync(
                cancellationToken);
            if (!recovered || File.Exists(paths.TransactionFile))
            {
                throw new ApplicationDataMigrationException(
                    "旧版账号切换事务无法安全恢复。请完全关闭 ChatGPT 后重试。");
            }
        }

        var credentialStore = new DeepSeekCredentialStore(paths.Root);
        var deepSeekStore = new DeepSeekConnectionStore(paths, credentialStore);
        var qwenCredentialStore = new QwenCredentialStore(paths.Root);
        var qwenStore = new QwenConnectionStore(paths, qwenCredentialStore);
        var configService = new DeepSeekCodexConfigService(
            new DeepSeekCodexConfigOptions(
                paths.CodexConfigFile,
                paths.DeepSeekModelCatalogFile,
                paths.DeepSeekConfigStateFile,
                credentialHelperPath));
        var connectionSwitchCoordinator = new ConnectionSwitchCoordinator(
            vault,
            deepSeekStore,
            credentialStore,
            configService,
            accountSwitchCoordinator,
            processController,
            operationGate,
            logger,
            qwenStore: qwenStore,
            qwenCredentialStore: qwenCredentialStore);
        await connectionSwitchCoordinator.RecoverProviderStateAsync(cancellationToken);

        if (configService.IsApplied)
        {
            var target = await FindLiveChatGptProfileAsync(
                paths,
                vault,
                cancellationToken) ?? throw new ApplicationDataMigrationException(
                "DeepSeek 正在启用，但无法识别可恢复的 ChatGPT 账号。请先用旧版切回 ChatGPT。");
            var result = await connectionSwitchCoordinator.SwitchToChatGptAsync(
                target.Id,
                forceConfigRestore: false,
                cancellationToken: cancellationToken);
            if (!result.IsSuccess || File.Exists(paths.DeepSeekConfigStateFile))
            {
                throw new ApplicationDataMigrationException(
                    "迁移前无法安全退出旧版 DeepSeek Provider。请先用旧版切回 ChatGPT。");
            }
        }

        if (await vault.GetActiveProfileAsync(cancellationToken) is null)
        {
            var live = await FindLiveChatGptProfileAsync(paths, vault, cancellationToken);
            if (live is not null)
            {
                await vault.SetActiveProfileAsync(live.Id, cancellationToken);
            }
        }
    }

    private static async Task<Models.AccountProfile?> FindLiveChatGptProfileAsync(
        AppPaths paths,
        ProfileVault vault,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.LiveAuthFile))
        {
            return null;
        }

        var liveAuth = await File.ReadAllBytesAsync(
            paths.LiveAuthFile,
            cancellationToken);
        try
        {
            var accountId = AuthDocument.Inspect(liveAuth).StoredAccountId;
            return string.IsNullOrWhiteSpace(accountId)
                ? null
                : await vault.FindByAccountIdAsync(accountId, cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(liveAuth);
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    public void ExitApplication()
    {
        PrepareForExit();
        if (_mainWindow is not null)
        {
            _mainWindow.Close();
            return;
        }

        Shutdown();
    }

    public void PrepareForExit()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _viewModel?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PrepareForExit();
        CodexAppServerClient.TerminateRunningProcesses();
        _viewModel?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _deepSeekHttpClient?.Dispose();
        _deepSeekHttpClient = null;
        _qwenHttpClient?.Dispose();
        _qwenHttpClient = null;
        _accountDetailsHttpClient?.Dispose();
        _accountDetailsHttpClient = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private static void CleanupTemp(string tempRoot)
    {
        if (!Directory.Exists(tempRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(tempRoot))
        {
            QuotaService.DeleteDirectoryBestEffort(directory);
        }

        foreach (var file in Directory.EnumerateFiles(tempRoot))
        {
            AtomicFile.TryDelete(file);
        }
    }
}
