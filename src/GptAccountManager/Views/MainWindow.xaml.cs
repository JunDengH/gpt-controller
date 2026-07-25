using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GptAccountManager.Infrastructure;
using GptAccountManager.ViewModels;
using MahApps.Metro.IconPacks;

namespace GptAccountManager.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var windowHandle = new WindowInteropHelper(this).Handle;
        WindowsDwm.TryApplyDefaultRoundedCorners(windowHandle);
        WindowsDwm.TrySetSystemBorderColor(windowHandle, 0x3D, 0x3E, 0x41);
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
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

}
