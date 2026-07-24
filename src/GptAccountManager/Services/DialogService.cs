using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GptAccountManager.Services;

public sealed class DialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public bool Ask(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Info(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    public void Error(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    public string? Prompt(string title, string label, string initialValue)
    {
        var input = new TextBox
        {
            Text = initialValue,
            MinWidth = 320,
            Margin = new Thickness(0, 8, 0, 18),
            Padding = new Thickness(8, 6, 8, 6)
        };
        var ok = new Button
        {
            Content = "保存",
            IsDefault = true,
            MinWidth = 82,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 82
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel
        {
            Margin = new Thickness(22)
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 14
        });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            Background = new SolidColorBrush(Color.FromRgb(23, 26, 34)),
            Foreground = Brushes.White,
            Content = panel,
            ShowInTaskbar = false
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return window.ShowDialog() == true
            ? input.Text.Trim()
            : null;
    }
}
