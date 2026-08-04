using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace GptController.Views;

public sealed record DeepSeekConnectionInput(string Nickname, string? ApiKey);

public partial class DeepSeekConnectionDialog : Window
{
    private readonly bool _hasExistingKey;

    public DeepSeekConnectionDialog(string nickname, bool hasExistingKey)
    {
        _hasExistingKey = hasExistingKey;
        InitializeComponent();
        NicknameTextBox.Text = string.IsNullOrWhiteSpace(nickname)
            ? "DeepSeek V4"
            : nickname;
        if (hasExistingKey)
        {
            KeyHintText.Text = "留空将继续使用已加密保存的 Key；输入新 Key 会完成验证后替换。";
        }

        AutomationProperties.SetIsRequiredForForm(
            ApiKeyPasswordBox,
            !hasExistingKey);

        UpdateSaveButton();
    }

    public DeepSeekConnectionInput Value => new(
        NicknameTextBox.Text.Trim(),
        string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
            ? null
            : ApiKeyPasswordBox.Password.Trim());

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        NicknameTextBox.Focus();
        NicknameTextBox.SelectAll();
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => UpdateSaveButton();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenApiKeys_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://platform.deepseek.com/api_keys",
            UseShellExecute = true
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            // The pointer may be released before WPF enters DragMove.
        }
    }

    private void UpdateSaveButton()
    {
        if (SaveButton is null || NicknameTextBox is null || ApiKeyPasswordBox is null)
        {
            return;
        }

        SaveButton.IsEnabled =
            !string.IsNullOrWhiteSpace(NicknameTextBox.Text) &&
            (_hasExistingKey || !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password));

        AutomationProperties.SetHelpText(
            SaveButton,
            SaveButton.IsEnabled
                ? "验证 API Key 和余额并安全保存连接"
                : _hasExistingKey
                    ? "输入连接名称后即可验证并保存"
                    : "输入连接名称和 API Key 后即可验证并保存");
    }
}
