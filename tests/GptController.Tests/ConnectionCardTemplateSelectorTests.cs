using System.Windows;
using GptController.Models;
using GptController.ViewModels;
using GptController.Views;

namespace GptController.Tests;

public sealed class ConnectionCardTemplateSelectorTests
{
    [Fact]
    public void SelectsDedicatedTemplateForEachConnectionKind()
    {
        var oauthTemplate = new DataTemplate();
        var apiTemplate = new DataTemplate();
        var selector = new ConnectionCardTemplateSelector
        {
            OAuthTemplate = oauthTemplate,
            ApiTemplate = apiTemplate
        };
        var oauthCard = new AccountCardViewModel(new AccountProfile
        {
            AccountId = "oauth-account",
            Nickname = "OAuth",
            Email = "oauth@example.com"
        });
        var apiCard = new AccountCardViewModel(new DeepSeekConnection());

        Assert.Same(oauthTemplate, selector.SelectTemplate(oauthCard, null!));
        Assert.Same(apiTemplate, selector.SelectTemplate(apiCard, null!));
    }

    [Fact]
    public void HomeKeepsProviderActionsAndQuotaBindings()
    {
        var xaml = ReadMainWindowXaml();
        Assert.Contains("ItemsSource=\"{Binding Accounts}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedConnection, Mode=TwoWay}\"", xaml);
        Assert.Contains("SelectApiModelCommand", xaml);
        Assert.Contains("SwitchAccountCommand", xaml);
        Assert.Contains("RefreshAccountCommand", xaml);
        Assert.Contains("DeleteAccountCommand", xaml);
        Assert.Contains("FiveHourRemainingValue, Mode=OneWay", xaml);
        Assert.Contains("WeeklyRemainingValue, Mode=OneWay", xaml);
        Assert.Contains("CreditBalanceText", xaml);
        Assert.Contains("DetailUpdatedText", xaml);
        Assert.Contains("SaveSettingsCommand", xaml);
    }

    [Fact]
    public void ApiDialogsKeepDarkThemeAndCurrentModelAffordance()
    {
        var selector = ReadViewXaml("ApiModelSelectionDialog.xaml");
        var qwen = ReadViewXaml("QwenConnectionDialog.xaml");

        Assert.Contains(
            "Background=\"{StaticResource SurfaceRaisedBrush}\"",
            selector,
            StringComparison.Ordinal);
        Assert.Contains("IsCurrent", selector, StringComparison.Ordinal);
        Assert.Contains("Text=\"当前\"", selector, StringComparison.Ordinal);
        Assert.Contains("Text=\"快照\"", selector, StringComparison.Ordinal);
        Assert.Contains("DialogComboBoxStyle", qwen, StringComparison.Ordinal);
        Assert.Contains("SelectedItem.DisplayName", qwen, StringComparison.Ordinal);
        Assert.Contains("仅支持按量付费 API Key", qwen, StringComparison.Ordinal);
    }

    private static string ReadMainWindowXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "GptController",
                "Views",
                "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Could not locate MainWindow.xaml.");
    }

    private static string ReadViewXaml(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "GptController",
                "Views",
                fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName}.");
    }
}
