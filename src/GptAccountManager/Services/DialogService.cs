using System.Windows;
using GptAccountManager.Views;

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
        var dialog = new TextPromptDialog(title, label, initialValue)
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true
            ? dialog.Value
            : null;
    }

    public DeepSeekConnectionInput? PromptDeepSeekConnection(
        string nickname,
        bool hasExistingKey)
    {
        var dialog = new DeepSeekConnectionDialog(nickname, hasExistingKey)
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true
            ? dialog.Value
            : null;
    }
}
