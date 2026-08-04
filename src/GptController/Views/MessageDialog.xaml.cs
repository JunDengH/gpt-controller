using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace GptController.Views;

public enum MessageDialogKind
{
    Info,
    Warning,
    Error,
    Question
}

public enum MessageDialogButtons
{
    Ok,
    OkCancel,
    YesNo
}

public enum MessageDialogResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No
}

public sealed record MessageDialogOptions(
    string Title,
    string Message,
    MessageDialogKind Kind,
    MessageDialogButtons Buttons = MessageDialogButtons.Ok,
    string? PrimaryButtonText = null,
    string? SecondaryButtonText = null,
    bool IsPrimaryDangerous = false);

public partial class MessageDialog : Window
{
    private readonly MessageDialogResult _primaryResult;
    private readonly MessageDialogResult _safeResult;
    private readonly Button _safeDefaultButton;

    public MessageDialog(MessageDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();

        Title = options.Title;
        DialogTitleText.Text = options.Title;
        MessageText.Text = options.Message;
        ApplyKind(options.Kind);

        (_primaryResult, _safeResult, _safeDefaultButton) =
            ConfigureButtons(options);
        AutomationProperties.SetHelpText(_safeDefaultButton, options.Message);
    }

    public MessageDialogResult Result { get; private set; }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _safeDefaultButton.Focus();
        Keyboard.Focus(_safeDefaultButton);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Result == MessageDialogResult.None)
        {
            Result = _safeResult;
        }

        base.OnClosing(e);
    }

    private void ApplyKind(MessageDialogKind kind)
    {
        var presentation = kind switch
        {
            MessageDialogKind.Warning => new DialogPresentation(
                PackIconMaterialKind.AlertOutline,
                "WarningAccentBrush",
                "WarningTintBrush"),
            MessageDialogKind.Error => new DialogPresentation(
                PackIconMaterialKind.AlertCircleOutline,
                "DangerBrush",
                "DangerTintBrush"),
            MessageDialogKind.Question => new DialogPresentation(
                PackIconMaterialKind.HelpCircleOutline,
                "AccentBrush",
                "InfoTintBrush"),
            _ => new DialogPresentation(
                PackIconMaterialKind.InformationOutline,
                "AccentBrush",
                "InfoTintBrush")
        };

        DialogIcon.Kind = presentation.Icon;
        DialogIcon.Foreground = (Brush)FindResource(presentation.ForegroundResourceKey);
        IconBackground.Background = (Brush)FindResource(presentation.BackgroundResourceKey);
    }

    private (MessageDialogResult Primary, MessageDialogResult Safe, Button SafeButton)
        ConfigureButtons(MessageDialogOptions options)
    {
        PrimaryButton.Style = (Style)FindResource(
            options.IsPrimaryDangerous
                ? "DialogDangerButtonStyle"
                : "DialogPrimaryButtonStyle");

        switch (options.Buttons)
        {
            case MessageDialogButtons.OkCancel:
                PrimaryButton.Content = options.PrimaryButtonText ?? "继续";
                SecondaryButton.Content = options.SecondaryButtonText ?? "取消";
                SecondaryButton.IsCancel = true;
                SecondaryButton.IsDefault = true;
                return (
                    MessageDialogResult.Ok,
                    MessageDialogResult.Cancel,
                    SecondaryButton);

            case MessageDialogButtons.YesNo:
                PrimaryButton.Content = options.PrimaryButtonText ?? "是";
                SecondaryButton.Content = options.SecondaryButtonText ?? "否";
                SecondaryButton.IsCancel = true;
                SecondaryButton.IsDefault = true;
                return (
                    MessageDialogResult.Yes,
                    MessageDialogResult.No,
                    SecondaryButton);

            default:
                PrimaryButton.Content = options.PrimaryButtonText ?? "知道了";
                PrimaryButton.IsDefault = true;
                PrimaryButton.IsCancel = true;
                SecondaryButton.Visibility = Visibility.Collapsed;
                return (
                    MessageDialogResult.Ok,
                    MessageDialogResult.Ok,
                    PrimaryButton);
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        Complete(_primaryResult);

    private void Secondary_Click(object sender, RoutedEventArgs e) =>
        Complete(_safeResult);

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Complete(_safeResult);

    private void Complete(MessageDialogResult result)
    {
        Result = result;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The pointer can be released before WPF enters DragMove.
        }
    }

    private sealed record DialogPresentation(
        PackIconMaterialKind Icon,
        string ForegroundResourceKey,
        string BackgroundResourceKey);
}
