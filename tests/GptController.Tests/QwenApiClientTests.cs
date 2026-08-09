using System.Net;
using System.Text;
using System.Text.Json;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class QwenApiClientTests
{
    private const string ApiKey = "sk-qwen-test-secret-1234";

    [Theory]
    [InlineData(QwenRegion.Beijing, "workspace-a", "https://workspace-a.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/")]
    [InlineData(QwenRegion.Singapore, "workspace-a", "https://workspace-a.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/")]
    [InlineData(QwenRegion.Virginia, null, "https://dashscope-us.aliyuncs.com/compatible-mode/v1/")]
    [InlineData(QwenRegion.Frankfurt, "workspace-a", "https://workspace-a.eu-central-1.maas.aliyuncs.com/compatible-mode/v1/")]
    [InlineData(QwenRegion.Tokyo, "workspace-a", "https://workspace-a.ap-northeast-1.maas.aliyuncs.com/compatible-mode/v1/")]
    public void RegionBuildsOnlyOfficialEndpoint(
        QwenRegion region,
        string? workspaceId,
        string expected)
    {
        Assert.Equal(expected, QwenRegions.Get(region).CreateBaseUrl(workspaceId).AbsoluteUri);
    }

    [Fact]
    public void WorkspaceRegionRejectsUnsafeOrMissingWorkspaceId()
    {
        Assert.Throws<ArgumentException>(() =>
            QwenRegions.Get(QwenRegion.Beijing).CreateBaseUrl(null));
        Assert.Throws<ArgumentException>(() =>
            QwenRegions.Get(QwenRegion.Tokyo).CreateBaseUrl("bad.example.com"));
    }

    [Fact]
    public async Task GetModelsUsesRegionEndpointAndReturnsOnlySortedQwenModels()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "data": [
                { "id": "text-embedding-v4" },
                { "id": "qwen3-plus-2026-07-28" },
                { "id": "qwen3-coder-plus" },
                { "id": "qwen3-coder-plus" },
                { "id": "QWEN3.8-MAX" },
                { "id": "qwen3.8-max" }
              ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new QwenApiClient(httpClient);

        var models = await client.GetModelsAsync(
            ApiKey,
            QwenRegion.Beijing,
            "workspace-a");

        Assert.Equal(
            "https://workspace-a.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/models",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal(ApiKey, handler.AuthorizationParameter);
        Assert.Equal(
            ["qwen3-coder-plus", "QWEN3.8-MAX", "qwen3-plus-2026-07-28"],
            models.Select(item => item.Id));
        Assert.False(models[0].IsSnapshot);
        Assert.True(models[^1].IsSnapshot);
    }

    [Fact]
    public async Task DedicatedPlanKeyIsRejectedBeforeAnyNetworkRequest()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{ "data": [] }"""));
        using var httpClient = new HttpClient(handler);
        var client = new QwenApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetModelsAsync(
                "sk-sp-dedicated-plan-key",
                QwenRegion.Virginia,
                null));

        Assert.Contains("按量付费", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task HttpClientTimeoutIsReportedWithoutLeakingTheKey()
    {
        using var httpClient = new HttpClient(new HangingHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(30)
        };
        var client = new QwenApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<QwenApiException>(() =>
            client.GetModelsAsync(ApiKey, QwenRegion.Virginia, null));

        Assert.Equal(QwenApiErrorKind.Timeout, exception.ErrorKind);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateModelRequiresResponsesFunctionCall()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "id": "resp_qwen",
              "output": [
                {
                  "type": "function_call",
                  "name": "codex_compatibility_check",
                  "call_id": "call_1",
                  "arguments": "{}"
                }
              ],
              "usage": { "input_tokens": 10, "output_tokens": 2, "total_tokens": 12 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new QwenApiClient(httpClient);

        var result = await client.ValidateModelAsync(
            ApiKey,
            QwenRegion.Virginia,
            null,
            "qwen3-coder-plus");

        Assert.Equal(
            "https://dashscope-us.aliyuncs.com/compatible-mode/v1/responses",
            handler.RequestUri?.AbsoluteUri);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("qwen3-coder-plus", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "codex_compatibility_check",
            request.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("resp_qwen", result.ResponseId);
        Assert.Equal(12, result.TotalTokens);
    }

    [Fact]
    public async Task TextOnlyResponseIsRejectedAsCodexIncompatible()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """{ "id": "resp_text", "output": [{ "type": "message" }] }"""));
        using var httpClient = new HttpClient(handler);
        var client = new QwenApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<QwenApiException>(() =>
            client.ValidateModelAsync(
                ApiKey,
                QwenRegion.Virginia,
                null,
                "qwen3-coder-plus"));

        Assert.Equal(QwenApiErrorKind.IncompatibleModel, exception.ErrorKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, QwenApiErrorKind.AuthenticationRequired)]
    [InlineData(HttpStatusCode.Forbidden, QwenApiErrorKind.AuthenticationRequired)]
    [InlineData(HttpStatusCode.PaymentRequired, QwenApiErrorKind.PaymentRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, QwenApiErrorKind.RateLimited)]
    public async Task HttpFailuresAreMappedWithoutLeakingResponseBody(
        HttpStatusCode status,
        QwenApiErrorKind expected)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent($"echo {ApiKey}")
        });
        using var httpClient = new HttpClient(handler);
        var client = new QwenApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<QwenApiException>(() =>
            client.GetModelsAsync(ApiKey, QwenRegion.Virginia, null));

        Assert.Equal(expected, exception.ErrorKind);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
