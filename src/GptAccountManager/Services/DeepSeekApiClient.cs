using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed class DeepSeekApiClient : IDeepSeekApiClient
{
    private static readonly Uri BalanceEndpoint =
        new("https://api.deepseek.com/user/balance");
    private static readonly Uri ResponsesEndpoint =
        new("https://api.deepseek.com/responses");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;

    public DeepSeekApiClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<DeepSeekBalanceSnapshot> GetBalanceAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ValidateApiKey(apiKey);
        using var request = CreateRequest(HttpMethod.Get, BalanceEndpoint, apiKey);
        using var response = await SendAsync(request, cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("is_available", out var availableElement) ||
                availableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("balance_infos", out var balancesElement) ||
                balancesElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse("DeepSeek 余额响应格式无效。");
            }

            var balances = new List<DeepSeekBalanceInfo>();
            foreach (var balanceElement in balancesElement.EnumerateArray())
            {
                balances.Add(new DeepSeekBalanceInfo
                {
                    Currency = RequiredString(balanceElement, "currency"),
                    TotalBalance = RequiredDecimal(balanceElement, "total_balance"),
                    GrantedBalance = RequiredDecimal(balanceElement, "granted_balance"),
                    ToppedUpBalance = RequiredDecimal(balanceElement, "topped_up_balance")
                });
            }

            return new DeepSeekBalanceSnapshot
            {
                IsAvailable = availableElement.GetBoolean(),
                Balances = balances,
                FetchedAt = DateTimeOffset.UtcNow
            };
        }
        catch (DeepSeekApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidResponse("DeepSeek 余额响应不是有效 JSON。");
        }
        catch (InvalidOperationException)
        {
            throw InvalidResponse("DeepSeek 余额响应格式无效。");
        }
    }

    public async Task<DeepSeekResponseTestResult> TestResponseAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ValidateApiKey(apiKey);
        using var request = CreateRequest(HttpMethod.Post, ResponsesEndpoint, apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new ResponseTestRequest(
                    DeepSeekDefaults.Model,
                    "Reply with OK only.",
                    16,
                    new ResponseReasoning("none")),
                JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await SendAsync(request, cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("DeepSeek Responses 响应格式无效。");
            }

            var responseId = RequiredString(root, "id");
            var outputText = ReadOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                throw InvalidResponse("DeepSeek Responses 响应缺少文本输出。");
            }

            int? inputTokens = null;
            int? outputTokens = null;
            int? totalTokens = null;
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = OptionalInt32(usage, "input_tokens");
                outputTokens = OptionalInt32(usage, "output_tokens");
                totalTokens = OptionalInt32(usage, "total_tokens");
            }

            return new DeepSeekResponseTestResult
            {
                ResponseId = responseId,
                OutputText = outputText,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens
            };
        }
        catch (DeepSeekApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidResponse("DeepSeek Responses 响应不是有效 JSON。");
        }
        catch (InvalidOperationException)
        {
            throw InvalidResponse("DeepSeek Responses 响应格式无效。");
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri endpoint,
        string apiKey)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            throw CreateHttpFailure(statusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Timeout,
                "DeepSeek 请求超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Timeout,
                "DeepSeek 请求超时。");
        }
        catch (HttpRequestException)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Network,
                "无法连接 DeepSeek API，请检查网络后重试。");
        }
        catch (IOException)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Network,
                "无法连接 DeepSeek API，请检查网络后重试。");
        }
        catch (DeepSeekApiException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Network,
                "无法连接 DeepSeek API，请检查网络后重试。");
        }
    }

    private static async Task<byte[]> ReadPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Timeout,
                "DeepSeek 请求超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Timeout,
                "DeepSeek 请求超时。");
        }
        catch (HttpRequestException)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Network,
                "读取 DeepSeek API 响应失败，请检查网络后重试。");
        }
        catch (IOException)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Network,
                "读取 DeepSeek API 响应失败，请检查网络后重试。");
        }
        catch (DeepSeekApiException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new DeepSeekApiException(
                DeepSeekApiErrorKind.Network,
                "读取 DeepSeek API 响应失败，请检查网络后重试。");
        }
    }

    private static DeepSeekApiException CreateHttpFailure(HttpStatusCode statusCode)
    {
        var numericStatus = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new DeepSeekApiException(
                DeepSeekApiErrorKind.AuthenticationRequired,
                "DeepSeek API Key 无效或已失效。",
                numericStatus),
            HttpStatusCode.PaymentRequired => new DeepSeekApiException(
                DeepSeekApiErrorKind.PaymentRequired,
                "DeepSeek 账户余额不足。",
                numericStatus),
            HttpStatusCode.TooManyRequests => new DeepSeekApiException(
                DeepSeekApiErrorKind.RateLimited,
                "DeepSeek 请求过于频繁，请稍后重试。",
                numericStatus),
            _ => new DeepSeekApiException(
                DeepSeekApiErrorKind.RemoteService,
                $"DeepSeek API 请求失败（HTTP {numericStatus}）。",
                numericStatus)
        };
    }

    private static string ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var directText) &&
            directText.ValueKind == JsonValueKind.String)
        {
            return directText.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var fragments = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String &&
                    string.Equals(
                        typeElement.GetString(),
                        "output_text",
                        StringComparison.Ordinal) &&
                    part.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String &&
                    textElement.GetString() is { Length: > 0 } text)
                {
                    fragments.Add(text);
                }
            }
        }

        return string.Concat(fragments);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw InvalidResponse($"DeepSeek 响应缺少字段 {propertyName}。");
    }

    private static decimal RequiredDecimal(JsonElement element, string propertyName)
    {
        var value = RequiredString(element, propertyName);
        if (decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var result))
        {
            return result;
        }

        throw InvalidResponse($"DeepSeek 响应字段 {propertyName} 不是有效金额。");
    }

    private static int? OptionalInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        throw InvalidResponse($"DeepSeek 响应字段 {propertyName} 不是有效整数。");
    }

    private static DeepSeekApiException InvalidResponse(string message) =>
        new(DeepSeekApiErrorKind.InvalidResponse, message);

    private static void ValidateApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("DeepSeek API Key 不能为空。", nameof(apiKey));
        }

        if (apiKey.Contains('\r') || apiKey.Contains('\n'))
        {
            throw new ArgumentException("DeepSeek API Key 格式无效。", nameof(apiKey));
        }

        if (apiKey.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("DeepSeek API Key 格式无效。", nameof(apiKey));
        }
    }

    private sealed record ResponseTestRequest(
        string Model,
        string Input,
        int MaxOutputTokens,
        ResponseReasoning Reasoning);

    private sealed record ResponseReasoning(string Effort);
}
