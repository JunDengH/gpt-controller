using System.Net.Http;
using System.Windows;
using GptAccountManager.Credentials;
using GptAccountManager.Infrastructure;
using GptAccountManager.Services;
using GptAccountManager.ViewModels;
using GptAccountManager.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace GptAccountManager;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MainWindowViewModel? _viewModel;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private HttpClient? _deepSeekHttpClient;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        _singleInstance = new Mutex(
            initiallyOwned: true,
            @"Local\GptAccountManager.Application",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "GPT Account Manager 已经在运行。",
                "GPT Account Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var paths = new AppPaths();
            paths.EnsureCreated();
            CleanupTemp(paths.Temp);

            var logger = new RedactingLogger(paths);
            var vault = new ProfileVault(paths);
            var settingsService = new SettingsService(paths);
            var configService = new CodexConfigService(paths);
            var metadataService = new AccountMetadataService();
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
            var credentialHelperPath = Path.Combine(
                AppContext.BaseDirectory,
                CredentialHelperLocator.ExecutableName);
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
            var codexVersionService = new CodexVersionService(locator);
            var connectionSwitchCoordinator = new ConnectionSwitchCoordinator(
                vault,
                deepSeekStore,
                deepSeekCredentialStore,
                deepSeekConfigService,
                switchCoordinator,
                processController,
                operationGate,
                logger);
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
                credentialHelperPath,
                isUiPreview);
            _mainWindow = new MainWindow(_viewModel);
            if (isCompactUiPreview)
            {
                _mainWindow.Width = 960;
                _mainWindow.Height = 680;
            }

            MainWindow = _mainWindow;
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
            MessageBox.Show(
                exception.Message,
                "GPT Account Manager 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
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
