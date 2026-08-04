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
    public void ApiTemplateUsesGenericFlatLedgerWithoutCredentialOrOAuthFields()
    {
        var xaml = ReadMainWindowXaml();
        const string startTag =
            "<views:ConnectionCardTemplateSelector.ApiTemplate>";
        const string endTag =
            "</views:ConnectionCardTemplateSelector.ApiTemplate>";
        var start = xaml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xaml.IndexOf(endTag, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "API card template was not found.");
        var template = xaml[start..(end + endTag.Length)];

        Assert.Contains("ApiPresentation.Model", template, StringComparison.Ordinal);
        Assert.Contains(
            "ApiPresentation.ProtocolDisplayName",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApiPresentation.EndpointHost",
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
        Assert.Contains(
            "ApiPresentation.PrimaryMetric.DetailText",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApiPresentation.LastValidatedText",
            template,
            StringComparison.Ordinal);
        Assert.Contains("Kind=\"CubeOutline\"", template, StringComparison.Ordinal);
        Assert.Contains("Kind=\"CodeTags\"", template, StringComparison.Ordinal);
        Assert.Contains("Kind=\"Web\"", template, StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource ApiCardIconButtonStyle}\"",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "TestApiConnectionCommand",
            template,
            StringComparison.Ordinal);
        Assert.Contains("SwitchAccountCommand", template, StringComparison.Ordinal);
        Assert.Contains("RefreshAccountCommand", template, StringComparison.Ordinal);
        Assert.Contains("RenameAccountCommand", template, StringComparison.Ordinal);
        Assert.Contains("DeleteAccountCommand", template, StringComparison.Ordinal);
        Assert.Equal(
            5,
            template.Split(
                "CommandParameter=\"{Binding}\"",
                StringSplitOptions.None).Length - 1);
        Assert.True(
            template.Split(
                "AutomationProperties.Name=",
                StringSplitOptions.None).Length - 1 >= 5);
        Assert.True(
            template.Split(
                "AutomationProperties.HelpText=",
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
            "USD",
            "CNY",
            "人民币",
            "¥"
        };
        Assert.All(
            forbiddenTerms,
            term => Assert.DoesNotContain(
                term,
                template,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OAuthTemplateDoesNotExposeApiConnectionDetailsOrActions()
    {
        var xaml = ReadMainWindowXaml();
        const string startTag =
            "<views:ConnectionCardTemplateSelector.OAuthTemplate>";
        const string endTag =
            "</views:ConnectionCardTemplateSelector.OAuthTemplate>";
        var start = xaml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xaml.IndexOf(endTag, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "OAuth card template was not found.");
        var template = xaml[start..(end + endTag.Length)];

        Assert.Contains("FiveHourRemainingValue", template, StringComparison.Ordinal);
        Assert.Contains("ProgressBar", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiPresentation.", template, StringComparison.Ordinal);
        Assert.DoesNotContain("EndpointHost", template, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TestApiConnectionCommand",
            template,
            StringComparison.Ordinal);
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
}
