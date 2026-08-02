using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GptController.Infrastructure;
using GptController.ViewModels;
using MahApps.Metro.IconPacks;

namespace GptController.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App app && !app.IsExiting)
        {
            if (ShouldMinimizeToTray(
                    app.IsExiting,
                    DataContext is MainWindowViewModel { CloseToTray: true }))
            {
                e.Cancel = true;
                Hide();
                return;
            }

            app.PrepareForExit();
        }

        base.OnClosing(e);
    }

    internal static bool ShouldMinimizeToTray(
        bool isExiting,
        bool closeToTray) =>
        !isExiting && closeToTray;

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

    private void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

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
