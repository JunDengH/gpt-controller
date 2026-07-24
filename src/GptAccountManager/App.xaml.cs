using System.Windows;
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

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            var processController = new ChatGptProcessController(locator, logger);
            var operationGate = new OperationGate();
            var quotaService = new QuotaService(
                paths,
                vault,
                locator,
                processController,
                metadataService,
                quotaParser,
                operationGate,
                logger);
            var oauthService = new OAuthAccountService(
                paths,
                vault,
                locator,
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
                dialogs);
            _mainWindow = new MainWindow(_viewModel);
            MainWindow = _mainWindow;
            _trayIcon = new TrayIconService(
                ShowMainWindow,
                ExitApplication,
                _viewModel.SwitchFromTrayAsync);
            _viewModel.AccountsChanged += (_, _) =>
                _trayIcon.UpdateAccounts(_viewModel.Accounts.ToList());

            _mainWindow.Show();
            await _viewModel.InitializeAsync();
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

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _viewModel?.Dispose();
        _trayIcon?.Dispose();
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _viewModel?.Dispose();
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
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
