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
    public void HomeUsesSplitAccountAndApiLanesWithInteractiveModelSelector()
    {
        var xaml = ReadMainWindowXaml();
        const string startTag = "ItemsSource=\"{Binding ApiConnections}\"";
        const string endTag = "</ItemsControl>";
        var start = xaml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xaml.IndexOf(endTag, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "API card template was not found.");
        var template = xaml[start..(end + endTag.Length)];

        Assert.Contains(
            "ItemsSource=\"{Binding ChatGptAccounts}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"连接管理\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("连接工作台", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "左侧管理 ChatGPT 身份与额度",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            xaml.Split("<Border Height=\"120\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split("<views:SelectionTraceBorder", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "Stroke=\"{Binding Background, ElementName=AccountAccentStripe}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Stroke=\"{Binding Background, ElementName=ApiAccentStripe}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Stroke=\"{StaticResource AccentBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            xaml.Split("Margin=\"-13,-10\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Margin=\"-12,-9\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ChatGptAccounts.Count, Mode=OneWay",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApiConnections.Count, Mode=OneWay",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{StaticResource BorderSubtleBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("ApiPresentation.Model", template, StringComparison.Ordinal);
        Assert.Contains("ProviderDisplayName", template, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApiPresentation.ProtocolDisplayName",
            template,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApiPresentation.EndpointHost",
            template,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApiModelDescription",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApiPresentation.PrimaryMetric.Label",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApiPresentation.PrimaryMetric.ValueText",
            template,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Kind=\"WalletOutline\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Kind=\"CubeOutline\"", template, StringComparison.Ordinal);
        Assert.Contains("SelectApiModelCommand", template, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectFlashModelCommand", template, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectProModelCommand", template, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFlashModel", template, StringComparison.Ordinal);
        Assert.DoesNotContain("IsProModel", template, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TestApiConnectionCommand",
            template,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            template.Split("<RowDefinition Height=\"*\" />", StringSplitOptions.None).Length - 1);
        Assert.Contains("Grid.RowSpan=\"4\"", template, StringComparison.Ordinal);
        Assert.Contains("SwitchAccountCommand", template, StringComparison.Ordinal);
        Assert.Contains("RefreshAccountCommand", template, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameAccountCommand", template, StringComparison.Ordinal);
        Assert.Contains("DeleteAccountCommand", template, StringComparison.Ordinal);
        Assert.Equal(
            4,
            template.Split(
                "CommandParameter=\"{Binding}\"",
                StringSplitOptions.None).Length - 1);
        Assert.True(
            template.Split(
                "AutomationProperties.Name=",
                StringSplitOptions.None).Length - 1 >= 5);
        var forbiddenTerms = new[]
        {
            "ProgressBar",
            "PlanDisplayName",
            "FiveHour",
            "Weekly",
            "MaskedApiKey",
            "KeyLastFour",
            "API Key",
            "Credential",
            "USD"
        };
        Assert.All(
            forbiddenTerms,
            term => Assert.DoesNotContain(
                term,
                template,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChatGptLaneKeepsBothQuotaWindowsAndExcludesApiActions()
    {
        var xaml = ReadMainWindowXaml();
        const string startTag = "ItemsSource=\"{Binding ChatGptAccounts}\"";
        const string endTag = "</ItemsControl>";
        var start = xaml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xaml.IndexOf(endTag, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "OAuth card template was not found.");
        var template = xaml[start..(end + endTag.Length)];

        Assert.Contains(
            "AutomationProperties.Name=\"{Binding Email}\"",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding Email, Mode=OneWay}\"",
            template,
            StringComparison.Ordinal);
        Assert.Contains("CompanyDisplayName", template, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnershipDisplayName", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"套餐\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"归属\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameAccountCommand", template, StringComparison.Ordinal);
        Assert.DoesNotContain("PencilOutline", template, StringComparison.Ordinal);
        Assert.Equal(
            2,
            template.Split("<RowDefinition Height=\"*\" />", StringSplitOptions.None).Length - 1);
        Assert.Contains("Grid.RowSpan=\"5\"", template, StringComparison.Ordinal);
        Assert.True(
            template.Split(
                "Foreground=\"{StaticResource AccentBrush}\"",
                StringSplitOptions.None).Length - 1 >= 2);
        Assert.Contains(
            "FiveHourRemainingValue, Mode=OneWay",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "WeeklyRemainingValue, Mode=OneWay",
            template,
            StringComparison.Ordinal);
        Assert.Contains("ProgressBar", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiPresentation.", template, StringComparison.Ordinal);
        Assert.DoesNotContain("EndpointHost", template, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TestApiConnectionCommand",
            template,
            StringComparison.Ordinal);
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
