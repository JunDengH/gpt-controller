using System.Net;
using System.Text;
using System.Text.Json;
using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class DeepSeekApiClientTests
{
    private const string ApiKey = "sk-test-never-log-1234";

    [Fact]
    public async Task GetBalanceUsesFixedEndpointAndParsesAllCurrencies()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "is_available": true,
              "balance_infos": [
                {
                  "currency": "CNY",
                  "total_balance": "110.25",
                  "granted_balance": "10.00",
                  "topped_up_balance": "100.25"
                },
                {
                  "currency": "USD",
                  "total_balance": "2.50",
                  "granted_balance": "0.50",
                  "topped_up_balance": "2.00"
                }
              ]
            }
            """));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://not-deepseek.example/")
        };
        var client = new DeepSeekApiClient(httpClient);

        var result = await client.GetBalanceAsync(ApiKey);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            "https://api.deepseek.com/user/balance",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(ApiKey, handler.AuthorizationParameter);
        Assert.True(result.IsAvailable);
        Assert.Collection(
            result.Balances,
            balance =>
            {
                Assert.Equal("CNY", balance.Currency);
                Assert.Equal(110.25m, balance.TotalBalance);
                Assert.Equal(10m, balance.GrantedBalance);
                Assert.Equal(100.25m, balance.ToppedUpBalance);
            },
            balance =>
            {
                Assert.Equal("USD", balance.Currency);
                Assert.Equal(2.5m, balance.TotalBalance);
                Assert.Equal(.5m, balance.GrantedBalance);
                Assert.Equal(2m, balance.ToppedUpBalance);
            });
        Assert.InRange(
            result.FetchedAt,
            DateTimeOffset.UtcNow.AddSeconds(-5),
            DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task TestResponseSendsMinimalNonStreamingResponsesRequest()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "id": "resp_test",
              "output": [
                {
                  "type": "reasoning",
                  "content": [
                    { "type": "reasoning_text", "text": "internal" }
                  ]
                },
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "OK" }
                  ]
                }
              ],
              "usage": {
                "input_tokens": 8,
                "output_tokens": 1,
                "total_tokens": 9
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var result = await client.TestResponseAsync(ApiKey);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "https://api.deepseek.com/responses",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal(ApiKey, handler.AuthorizationParameter);
        Assert.NotNull(handler.RequestBody);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            DeepSeekDefaults.Model,
            request.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "Reply with OK only.",
            request.RootElement.GetProperty("input").GetString());
        Assert.Equal(
            16,
            request.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(
            "none",
            request.RootElement
                .GetProperty("reasoning")
                .GetProperty("effort")
                .GetString());
        Assert.False(request.RootElement.TryGetProperty("stream", out _));
        Assert.Equal("resp_test", result.ResponseId);
        Assert.Equal("OK", result.OutputText);
        Assert.Equal(8, result.InputTokens);
        Assert.Equal(1, result.OutputTokens);
        Assert.Equal(9, result.TotalTokens);
    }

    [Fact]
    public async Task TestResponseAcceptsTopLevelOutputText()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "id": "resp_direct",
              "output_text": "OK"
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var result = await client.TestResponseAsync(ApiKey);

        Assert.Equal("resp_direct", result.ResponseId);
        Assert.Equal("OK", result.OutputText);
        Assert.Null(result.InputTokens);
        Assert.Null(result.OutputTokens);
        Assert.Null(result.TotalTokens);
    }

    [Theory]
    [MemberData(nameof(HttpFailureCases))]
    public async Task HttpFailuresAreMappedWithoutExposingResponseBody(
        HttpStatusCode statusCode,
        DeepSeekApiErrorKind expectedKind)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                $"{{\"error\":\"echoed {ApiKey}\"}}",
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<DeepSeekApiException>(
            () => client.GetBalanceAsync(ApiKey));

        Assert.Equal(expectedKind, exception.ErrorKind);
        Assert.Equal((int)statusCode, exception.StatusCode);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<HttpStatusCode, DeepSeekApiErrorKind> HttpFailureCases =>
        new()
        {
            { HttpStatusCode.Unauthorized, DeepSeekApiErrorKind.AuthenticationRequired },
            { HttpStatusCode.PaymentRequired, DeepSeekApiErrorKind.PaymentRequired },
            { HttpStatusCode.TooManyRequests, DeepSeekApiErrorKind.RateLimited },
            { HttpStatusCode.InternalServerError, DeepSeekApiErrorKind.RemoteService }
        };

    [Fact]
    public async Task TimeoutIsMappedAndUnderlyingMessageCannotLeakApiKey()
    {
        var handler = new RecordingHandler(
            _ => throw new TaskCanceledException($"timeout for {ApiKey}"));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<DeepSeekApiException>(
            () => client.TestResponseAsync(ApiKey));

        Assert.Equal(DeepSeekApiErrorKind.Timeout, exception.ErrorKind);
        Assert.Null(exception.StatusCode);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task NetworkFailureIsMappedAndUnderlyingMessageCannotLeakApiKey()
    {
        var handler = new RecordingHandler(
            _ => throw new HttpRequestException($"network failure for {ApiKey}"));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<DeepSeekApiException>(
            () => client.GetBalanceAsync(ApiKey));

        Assert.Equal(DeepSeekApiErrorKind.Network, exception.ErrorKind);
        Assert.Null(exception.StatusCode);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task UnexpectedTransportFailureCannotLeakApiKey()
    {
        var handler = new RecordingHandler(
            _ => throw new InvalidOperationException($"transport bug for {ApiKey}"));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<DeepSeekApiException>(
            () => client.GetBalanceAsync(ApiKey));

        Assert.Equal(DeepSeekApiErrorKind.Network, exception.ErrorKind);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"secret\",\"granted_balance\":\"0\",\"topped_up_balance\":\"0\"}]}")]
    public async Task InvalidBalancePayloadHasStableSanitizedFailure(string payload)
    {
        var handler = new RecordingHandler(_ => JsonResponse(payload));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<DeepSeekApiException>(
            () => client.GetBalanceAsync(ApiKey));

        Assert.Equal(DeepSeekApiErrorKind.InvalidResponse, exception.ErrorKind);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task InvalidResponsePayloadDoesNotEchoPayloadOrApiKey()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            $"{{\"id\":\"resp_test\",\"output\":[],\"debug\":\"{ApiKey}\"}}"));
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<DeepSeekApiException>(
            () => client.TestResponseAsync(ApiKey));

        Assert.Equal(DeepSeekApiErrorKind.InvalidResponse, exception.ErrorKind);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToTimeout()
    {
        var handler = new RecordingHandler(
            _ => throw new OperationCanceledException());
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetBalanceAsync(ApiKey, cancellation.Token));
    }

    [Fact]
    public void ConnectionMetadataNeverContainsFullApiKey()
    {
        var connection = new DeepSeekConnection
        {
            KeyLastFour = "1234",
            IsAvailable = true,
            CnyBalance = 8.5m,
            UsdBalance = 1.25m,
            Status = DeepSeekConnectionStatus.Available,
            LastValidatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(connection);

        Assert.Equal(DeepSeekDefaults.Model, connection.Model);
        Assert.Equal(DeepSeekConnection.FixedId, connection.Id);
        Assert.Equal("•••• 1234", connection.MaskedApiKey);
        Assert.DoesNotContain(ApiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("MaskedApiKey", json, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ContentType { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
