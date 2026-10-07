using System.Net;
using System.Text;
using System.Text.Json;
using GptController.Models;
using GptController.Services;
using GptController.ViewModels;

namespace GptController.Tests;

public sealed class CurrentOfficialInformationTests
{
    [Theory]
    [InlineData("prolite", MembershipPlan.Pro5x, "Pro 5x")]
    [InlineData("pro_lite", MembershipPlan.Pro5x, "Pro 5x")]
    [InlineData("pro_5x", MembershipPlan.Pro5x, "Pro 5x")]
    [InlineData("pro", MembershipPlan.Pro10x, "Pro 10x")]
    [InlineData("pro_10x", MembershipPlan.Pro10x, "Pro 10x")]
    [InlineData("promax", MembershipPlan.Pro25x, "Pro 25x")]
    [InlineData("pro_25x", MembershipPlan.Pro25x, "Pro 25x")]
    [InlineData("pro_20x", MembershipPlan.Pro20x, "Pro（旧档位，待刷新）")]
    public void ProtocolProAliasesPreserveTheSubscriptionTier(string raw, MembershipPlan expected, string label)
    {
        Assert.Equal(expected, new AccountMetadataService().NormalizePlan(raw));
        Assert.Equal(label, Profile(expected).PlanDisplayName);
    }

    [Theory]
    [InlineData(MembershipPlan.Pro5x, "Pro 5x")]
    [InlineData(MembershipPlan.Pro20x, "Pro（旧档位，待刷新）")]
    [InlineData(MembershipPlan.Team, "Business")]
    public void OldSerializedPlansRemainReadableWithCurrentDisplayNames(MembershipPlan legacy, string display)
    {
        var restored = JsonSerializer.Deserialize<AccountProfile>(JsonSerializer.Serialize(Profile(legacy)))!;
        Assert.Equal(legacy, restored.MembershipPlan);
        Assert.Equal(display, restored.PlanDisplayName);
    }

    [Theory]
    [InlineData("edu_plus")]
    [InlineData("edu_pro")]
    public void CurrentCliEducationVariantsAreRecognized(string raw) =>
        Assert.Equal(MembershipPlan.Edu, new AccountMetadataService().NormalizePlan(raw));

    [Fact]
    public void ExplicitlyAbsentQuotaWindowsAreNotZeroOrOneHundredPercent()
    {
        using var payload = JsonDocument.Parse("""{"rateLimits":{"planType":"pro","primary":null,"secondary":null}}""");
        var snapshot = new QuotaParser().Parse(payload.RootElement, DateTimeOffset.UtcNow).Snapshot;
        var card = new AccountCardViewModel(Profile(MembershipPlan.Pro) with { Quota = snapshot });
        Assert.Equal(QuotaStatus.Fresh, snapshot.Status);
        Assert.Null(snapshot.FiveHourRemainingPercent);
        Assert.Null(snapshot.RemainingPercent);
        Assert.Equal("不适用", card.FiveHourRemainingText);
        Assert.False(card.ShowPrimaryProgress);
        Assert.False(card.ShowSecondaryProgress);
    }

    [Fact]
    public void ProWeeklyOnlyDataDoesNotCreateAFiveHourWindow()
    {
        using var payload = JsonDocument.Parse("""
            {"rateLimits":{"planType":"pro","primary":{"usedPercent":28,"windowDurationMins":10080},"secondary":null}}
            """);
        var snapshot = new QuotaParser().Parse(payload.RootElement, DateTimeOffset.UtcNow).Snapshot;
        var card = new AccountCardViewModel(Profile(MembershipPlan.Pro) with { Quota = snapshot });
        Assert.Equal("短期窗口", card.PrimaryMetricLabel);
        Assert.Equal("不适用", card.FiveHourRemainingText);
        Assert.Equal("72%", card.WeeklyRemainingText);
        Assert.False(card.ShowPrimaryProgress);
        Assert.True(card.ShowSecondaryProgress);
    }

    [Fact]
    public void DailyAndMonthlyWindowsKeepTheirActualDurations()
    {
        using var payload = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":1440},
                           "secondary":{"usedPercent":30,"windowDurationMins":43200}}}
            """);
        var snapshot = new QuotaParser().Parse(payload.RootElement, DateTimeOffset.UtcNow).Snapshot;
        var card = new AccountCardViewModel(Profile(MembershipPlan.Business) with { Quota = snapshot });
        Assert.Equal("1 天", card.PrimaryMetricLabel);
        Assert.Equal("30 天", card.SecondaryMetricLabel);
        Assert.Equal("90%", card.FiveHourRemainingText);
        Assert.Equal("70%", card.WeeklyRemainingText);
    }

    [Fact]
    public void MissingDurationDoesNotClaimAStandardFiveHourPeriod()
    {
        using var payload = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":10}}}""");
        var snapshot = new QuotaParser().Parse(payload.RootElement, DateTimeOffset.UtcNow).Snapshot;
        Assert.Equal("短期窗口", new AccountCardViewModel(Profile(MembershipPlan.Plus) with { Quota = snapshot }).PrimaryMetricLabel);
    }

    [Fact]
    public async Task DeepSeekOfficialCapabilitiesFlowIntoTheProviderDefinition()
    {
        using var http = Client("""
            {"data":[{"id":"deepseek-flash","name":"DeepSeek-V4.1-Flash","context_window":1048576,
             "max_output_tokens":393216,"input_modalities":["text","image"],
             "effort":{"supported_levels":["low","high","max"],"default_level":"high"}}]}
            """);
        var models = await new DeepSeekApiClient(http).GetModelsAsync("sk-test-1234");
        var provider = ApiProviderDefinitions.ForDeepSeek("deepseek-flash", models);
        Assert.Equal(1048576, provider.ConservativeContextWindow);
        Assert.Equal(393216, models[0].MaxOutputTokens);
        Assert.Contains("image", models[0].InputModalities);
        Assert.Equal(["low", "high", "max"], models[0].ReasoningEfforts);
        Assert.Equal("high", provider.DefaultReasoningEffort);
        Assert.True(provider.SupportsReasoning);
        Assert.False(provider.SupportsSearch);
    }

    [Fact]
    public void UnknownModelMetadataDoesNotEnableUnverifiedCapabilities()
    {
        var provider = ApiProviderDefinitions.ForDeepSeek("deepseek-future");
        Assert.Single(provider.Models);
        Assert.False(provider.SupportsReasoning);
        Assert.False(provider.SupportsSearch);
        Assert.Null(provider.DefaultReasoningEffort);
        Assert.Equal(32768, provider.ConservativeContextWindow);
    }

    [Fact]
    public async Task QwenNativeCatalogUsesDocumentedFiltersAndMetadata()
    {
        var handler = new MetadataHandler("""
            {"output":{"total":1,"models":[{"model":"qwen3.7-plus","name":"千问 Plus",
            "features":["function-calling"],"inference_metadata":{"request_modality":["Text","Image"]},
            "model_info":{"context_window":1000000,"max_output_tokens":65536}}]}}
            """);
        using var http = new HttpClient(handler);
        var models = await new QwenApiClient(http).GetModelsAsync("sk-test-1234", QwenRegion.HongKong, "workspace-a");
        Assert.Contains("providers=qwen", handler.Uri!.Query);
        Assert.Contains("capabilities=TG", handler.Uri.Query);
        Assert.Equal("workspace-a.cn-hongkong.maas.aliyuncs.com", handler.Uri.Host);
        Assert.Equal(1000000, models[0].ContextWindowTokens);
        Assert.Equal(65536, models[0].MaxOutputTokens);
        Assert.Contains("image", models[0].InputModalities);
        var provider = ApiProviderDefinitions.ForQwen(new QwenConnection
        { Model = models[0].Id, Models = models, Region = QwenRegion.HongKong, WorkspaceId = "workspace-a" });
        Assert.Equal(1000000, provider.ConservativeContextWindow);
    }

    [Theory]
    [InlineData(QwenRegion.Beijing, "dashscope.aliyuncs.com", "cn-beijing")]
    [InlineData(QwenRegion.Singapore, "dashscope-intl.aliyuncs.com", "ap-southeast-1")]
    [InlineData(QwenRegion.Virginia, "dashscope-us.aliyuncs.com", "us-east-1")]
    [InlineData(QwenRegion.HongKong, "cn-hongkong.dashscope.aliyuncs.com", "cn-hongkong")]
    public void SharedAndWorkspaceEndpointsRemainDistinct(QwenRegion region, string shared, string dedicated)
    {
        Assert.Equal(shared, QwenRegions.Get(region).CreateBaseUrl(null).Host);
        Assert.Equal($"workspace-a.{dedicated}.maas.aliyuncs.com", QwenRegions.Get(region).CreateBaseUrl("workspace-a").Host);
    }

    private static AccountProfile Profile(MembershipPlan plan) => new()
    { Nickname = "Test", Email = "test@example.com", AccountId = "test", MembershipPlan = plan };
    private static HttpClient Client(string payload) => new(new MetadataHandler(payload));
    private sealed class MetadataHandler(string payload) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
