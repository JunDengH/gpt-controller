using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using GptController.Models;

namespace GptController.Views;

public sealed record QwenConnectionInput(
    QwenRegion Region,
    string? WorkspaceId,
    string? ApiKey);

public partial class QwenConnectionDialog : Window
{
    private readonly bool _hasExistingKey;

    public QwenConnectionDialog(QwenConnection? existing)
    {
        _hasExistingKey = existing is not null;
        InitializeComponent();
        RegionComboBox.ItemsSource = QwenRegions.All;
        RegionComboBox.SelectedItem = QwenRegions.Get(
            existing?.Region ?? QwenRegion.Beijing);
        WorkspaceTextBox.Text = existing?.WorkspaceId ?? string.Empty;
        if (_hasExistingKey)
        {
            KeyHintText.Text = "留空将继续使用已加密保存的 Key；输入新 Key 会在验证成功后替换。";
        }

        AutomationProperties.SetIsRequiredForForm(ApiKeyPasswordBox, !_hasExistingKey);
        UpdateRegionState();
        UpdateSaveButton();
    }

    public QwenConnectionInput Value
    {
        get
        {
            var region = ((QwenRegionDefinition)RegionComboBox.SelectedItem).Region;
            return new(
                region,
                string.IsNullOrWhiteSpace(WorkspaceTextBox.Text) ? null : WorkspaceTextBox.Text.Trim(),
                string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                    ? null
                    : ApiKeyPasswordBox.Password.Trim());
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RegionComboBox.Focus();
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => UpdateSaveButton();

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateRegionState();
        UpdateSaveButton();
    }

    private void UpdateRegionState()
    {
        if (WorkspaceTextBox is null || RegionComboBox?.SelectedItem is not QwenRegionDefinition region)
        {
            return;
        }

        WorkspaceTextBox.IsEnabled = true;
        WorkspaceHintText.Text = region.RequiresWorkspaceId
            ? "必填。可在阿里云百炼控制台的业务空间详情中查看。"
            : "填写可使用业务空间专属域名（官方推荐）；留空使用该地域共享端点。";
        AutomationProperties.SetIsRequiredForForm(
            WorkspaceTextBox,
            region.RequiresWorkspaceId);
    }

    private void UpdateSaveButton()
    {
        if (SaveButton is null || RegionComboBox?.SelectedItem is not QwenRegionDefinition region)
        {
            return;
        }

        var workspaceValid = !region.RequiresWorkspaceId && string.IsNullOrWhiteSpace(WorkspaceTextBox.Text) ||
                             IsWorkspaceIdValid(WorkspaceTextBox.Text);
        SaveButton.IsEnabled = workspaceValid &&
                               (_hasExistingKey ||
                                !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password));
    }

    private static bool IsWorkspaceIdValid(string? value)
    {
        try
        {
            _ = QwenRegions.Get(QwenRegion.Beijing).CreateBaseUrl(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveButton.IsEnabled)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenApiKeys_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://bailian.console.aliyun.com/?apiKey=1#/api-key",
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
            // Pointer released before WPF entered DragMove.
        }
    }
}
