using System.Net;
using System.Text;
using System.Text.Json;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;
using GptController.ViewModels;

namespace GptController.Tests;

public sealed class OfficialDataRefreshTests
{
    [Fact]
    public async Task DeepSeekDiscoversNewModelsWithoutAClientUpdate()
    {
        var handler = new CatalogHandler(_ => JsonResponse("""
            {"data":[{"id":"deepseek-future","name":"Future Model","context_window":262144},
                     {"id":"deepseek-flash"},{"id":"deepseek-flash"}]}
            """));
        using var http = new HttpClient(handler);
        var models = await new DeepSeekApiClient(http).GetModelsAsync("sk-test-1234");
        Assert.Equal("https://api.deepseek.com/models", handler.Requests.Single().AbsoluteUri);
        Assert.Equal(["deepseek-flash", "deepseek-future"], models.Select(item => item.Id));
        Assert.Equal("Future Model", models[1].DisplayName);
        Assert.Equal(262144, models[1].ContextWindowTokens);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
        Assert.Equal("Bearer", handler.AuthorizationScheme);
    }

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"deepseek-bad\\nconfig\"}]}")]
    [InlineData("{\"data\":[{\"id\":\"other-model\"}]}")]
    [InlineData("{\"error\":\"sk-secret-must-not-appear\"}")]
    [InlineData("not json sk-secret-must-not-appear")]
    public async Task DeepSeekRejectsMalformedCatalogWithoutExposingPayload(string payload)
    {
        using var http = new HttpClient(new CatalogHandler(_ => JsonResponse(payload)));
        var error = await Assert.ThrowsAsync<DeepSeekApiException>(() =>
            new DeepSeekApiClient(http).GetModelsAsync("sk-test-1234"));
        Assert.Equal(DeepSeekApiErrorKind.InvalidResponse, error.ErrorKind);
        Assert.DoesNotContain("sk-secret", error.Message);
    }

    [Fact]
    public async Task QwenReadsEveryNativeCatalogPageAndKeepsOfficialNames()
    {
        var handler = new CatalogHandler(index => JsonResponse(index == 0
            ? """{"output":{"total":2,"models":[{"model":"qwen-future","name":"千问新模型"}]}}"""
            : """{"output":{"total":2,"models":[{"model":"qwen-plus-2026-09-01"}]}}"""));
        using var http = new HttpClient(handler);
        var models = await new QwenApiClient(http).GetModelsAsync("sk-test-1234", QwenRegion.Beijing, "workspace-a");
        Assert.Equal(2, models.Count);
        Assert.Equal("千问新模型", models[0].EffectiveDisplayName);
        Assert.True(models[1].IsSnapshot);
        Assert.Equal("workspace-a.cn-beijing.maas.aliyuncs.com", handler.Requests[0].Host);
        Assert.Equal("/api/v1/models", handler.Requests[0].AbsolutePath);
        Assert.Contains("page_no=2", handler.Requests[1].Query);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task QwenIncompletePaginationFailsInsteadOfSavingAPartialList()
    {
        var handler = new CatalogHandler(index => JsonResponse(index == 0
            ? """{"output":{"total":2,"models":[{"model":"qwen-future"}]}}"""
            : """{"output":{"total":2,"models":[]}}"""));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<QwenApiException>(() =>
            new QwenApiClient(http).GetModelsAsync("sk-test-1234", QwenRegion.Beijing, "workspace-a"));
        Assert.Equal(QwenApiErrorKind.InvalidResponse, error.ErrorKind);
    }

    [Fact]
    public async Task QwenOlderDeploymentsUseCompatibleCatalogOnlyOnNotFound()
    {
        var handler = new CatalogHandler(index => index == 0
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : JsonResponse("""{"data":[{"id":"qwen-future"}]}"""));
        using var http = new HttpClient(handler);
        var models = await new QwenApiClient(http).GetModelsAsync("sk-test-1234", QwenRegion.Beijing, "workspace-a");
        Assert.Single(models);
        Assert.Equal("/compatible-mode/v1/models", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotTryAnotherCatalogEndpoint()
    {
        var handler = new CatalogHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<QwenApiException>(() =>
            new QwenApiClient(http).GetModelsAsync("sk-test-1234", QwenRegion.Beijing, "workspace-a"));
        Assert.Equal(QwenApiErrorKind.AuthenticationRequired, error.ErrorKind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(15, 1, 2)]
    [InlineData(30, 1, 30)]
    [InlineData(15, 2, 15)]
    [InlineData(1, 2, 1)]
    public async Task SettingsUpgradeChangesOnlyTheOldDefault(int oldInterval, int policy, int expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"refresh-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(root, root);
            Directory.CreateDirectory(paths.Root);
            var settings = new AppSettings
            {
                QuotaRefreshMinutes = oldInterval, RefreshPolicyVersion = policy,
                CloseToTray = false, StartMinimized = true
            };
            var service = new SettingsService(paths);
            await service.SaveAsync(settings);
            var updated = await service.LoadAsync();
            Assert.Equal(expected, updated.QuotaRefreshMinutes);
            Assert.False(updated.CloseToTray);
            Assert.True(updated.StartMinimized);
            Assert.Equal(2, updated.RefreshPolicyVersion);
            Assert.Equal(updated, await service.LoadAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void QuotaKeepsOfficialCreditsAndVariableWindowLengths()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimitsByLimitId":{"codex":{"planType":"go",
             "primary":{"usedPercent":25,"windowDurationMins":15},
             "secondary":{"usedPercent":10,"windowDurationMins":10080},
             "credits":{"hasCredits":true,"unlimited":false,"balance":"57.5"}}}}
            """);
        var parsed = new QuotaParser().Parse(document.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal("57.5", parsed.Snapshot.CreditBalance);
        Assert.Equal(75, parsed.Snapshot.FiveHourRemainingPercent);
        Assert.Equal(15, parsed.Snapshot.FiveHourWindowDurationMinutes);
        var card = new AccountCardViewModel(new AccountProfile
        {
            Email = "test@example.com", AccountId = "test", Nickname = "Test", Quota = parsed.Snapshot
        });
        Assert.Equal("15 分钟", card.PrimaryMetricLabel);
        Assert.Equal("Credits · 57.5", card.CreditBalanceText);
    }

    [Fact]
    public void CreditsUnavailableIsDistinctFromZero()
    {
        var profile = new AccountProfile { Email = "test@example.com", AccountId = "test", Nickname = "Test" };
        Assert.Equal("Credits · 未提供", new AccountCardViewModel(profile).CreditBalanceText);
        Assert.Equal("Credits · 不可用", new AccountCardViewModel(profile with { Quota = new QuotaSnapshot { HasCredits = false } }).CreditBalanceText);
    }

    private static HttpResponseMessage JsonResponse(string payload) => new(HttpStatusCode.OK)
    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private sealed class CatalogHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public string? AuthorizationScheme { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Methods.Add(request.Method);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(response(Requests.Count - 1));
        }
    }
}
