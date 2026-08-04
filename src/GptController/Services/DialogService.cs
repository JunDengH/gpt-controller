using System.Windows;
using GptController.Views;

namespace GptController.Services;

public sealed class DialogService
{
    public bool Confirm(
        string title,
        string message,
        string primaryActionText = "继续",
        bool isDangerous = false,
        string cancelActionText = "取消") =>
        Show(
            new MessageDialogOptions(
                title,
                message,
                MessageDialogKind.Warning,
                MessageDialogButtons.OkCancel,
                primaryActionText,
                cancelActionText,
                isDangerous)) == MessageDialogResult.Ok;

    public bool Ask(
        string title,
        string message,
        string yesActionText = "是",
        string noActionText = "否") =>
        Show(
            new MessageDialogOptions(
                title,
                message,
                MessageDialogKind.Question,
                MessageDialogButtons.YesNo,
                yesActionText,
                noActionText)) == MessageDialogResult.Yes;

    public void Info(
        string title,
        string message,
        string actionText = "知道了") =>
        Show(
            new MessageDialogOptions(
                title,
                message,
                MessageDialogKind.Info,
                PrimaryButtonText: actionText));

    public void Error(
        string title,
        string message,
        string actionText = "关闭") =>
        Show(
            new MessageDialogOptions(
                title,
                message,
                MessageDialogKind.Error,
                PrimaryButtonText: actionText));

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

    public MessageDialogResult Show(MessageDialogOptions options)
    {
        var dialog = new MessageDialog(options);
        if (Application.Current?.MainWindow is { IsVisible: true } owner &&
            !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}
