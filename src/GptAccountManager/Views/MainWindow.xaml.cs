using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using GptAccountManager.ViewModels;
using MahApps.Metro.IconPacks;

namespace GptAccountManager.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        UpdateWindowFrame();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: false })
        {
            if (DataContext is MainWindowViewModel { CloseToTray: true })
            {
                e.Cancel = true;
                Hide();
                return;
            }

            e.Cancel = true;
            ((App)Application.Current).ExitApplication();
            return;
        }

        base.OnClosing(e);
    }

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released before WPF enters DragMove.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaximizeIcon.Kind = WindowState == WindowState.Maximized
            ? PackIconMaterialKind.WindowRestore
            : PackIconMaterialKind.WindowMaximize;
        UpdateWindowFrame();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowFrame()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowFrame.CornerRadius = isMaximized
            ? new CornerRadius(0)
            : new CornerRadius(14);
        TitleBarFrame.CornerRadius = isMaximized
            ? new CornerRadius(0)
            : new CornerRadius(13, 13, 0, 0);
        FooterFrame.CornerRadius = isMaximized
            ? new CornerRadius(0)
            : new CornerRadius(0, 0, 13, 13);

        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CornerRadius = isMaximized
                ? new CornerRadius(0)
                : new CornerRadius(14);
        }
    }
}
