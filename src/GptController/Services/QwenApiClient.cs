using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GptController.Models;

namespace GptController.Services;

public sealed class QwenApiClient : IQwenApiClient
{
    private const string CompatibilityToolName = "codex_compatibility_check";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;

    public QwenApiClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<ApiModelDescriptor>> GetModelsAsync(
        string apiKey,
        QwenRegion region,
        string? workspaceId,
        CancellationToken cancellationToken = default)
    {
        ValidateApiKey(apiKey);
        var allModels = new List<ApiModelDescriptor>();
        for (var page = 1; page <= 100; page++)
        {
            var (models, total) = await GetModelPageAsync(apiKey, region, workspaceId, page, cancellationToken);
            allModels.AddRange(models);
            if (total is null || allModels.Count >= total)
            {
                return allModels.Where(item => item.Id.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.IsSnapshot).ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            if (models.Count == 0)
            {
                throw InvalidResponse("千问模型列表分页不完整，请稍后重试。");
            }
        }
        throw InvalidResponse("千问模型列表分页超出范围。");
    }

    private async Task<(IReadOnlyList<ApiModelDescriptor> Models, int? Total)> GetModelPageAsync(
        string apiKey, QwenRegion region, string? workspaceId, int page, CancellationToken cancellationToken)
    {
        var baseUrl = CreateBaseUrl(region, workspaceId);
        var nativeEndpoint = new Uri(baseUrl, "/api/v1/models");
        var endpoint = new Uri(nativeEndpoint +
            $"?providers=qwen&capabilities=TG&page_no={page}&page_size=100");
        using var request = CreateRequest(HttpMethod.Get, endpoint, apiKey);
        using var response = await SendModelListAsync(request, baseUrl, page, apiKey, cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                throw InvalidResponse("千问官方接口未能完成模型查询，请稍后重试。");
            }
            JsonElement data;
            int? total = null;
            var output = default(JsonElement);
            var native = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("output", out output);
            if (native)
            {
                if (output.ValueKind != JsonValueKind.Object ||
                    !output.TryGetProperty("models", out data) || data.ValueKind != JsonValueKind.Array ||
                    !output.TryGetProperty("total", out var count) || !count.TryGetInt32(out var number) || number < 0)
                {
                    throw InvalidResponse("千问模型列表响应格式无效。");
                }
                total = number;
            }
            else if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse("千问模型列表响应格式无效。");
            }

            var models = data.EnumerateArray()
                .Select(item => ModelMetadataReader.Qwen(item, native ? RequiredString(item, "model") : ReadModelId(item)))
                .ToArray();
            return (models, total);
        }
        catch (QwenApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidResponse("千问模型列表响应不是有效 JSON。");
        }
        catch (InvalidOperationException)
        {
            throw InvalidResponse("千问模型列表响应格式无效。");
        }
    }

    public async Task<ApiValidationResult> ValidateModelAsync(
        string apiKey,
        QwenRegion region,
        string? workspaceId,
        string model,
        CancellationToken cancellationToken = default)
    {
        ValidateApiKey(apiKey);
        ValidateModel(model);
        var endpoint = new Uri(CreateBaseUrl(region, workspaceId), "responses");
        using var request = CreateRequest(HttpMethod.Post, endpoint, apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    model,
                    input = "Call codex_compatibility_check exactly once.",
                    max_output_tokens = 32,
                    tools = new object[]
                    {
                        new
                        {
                            type = "function",
                            name = CompatibilityToolName,
                            description = "Checks Responses API function calling compatibility.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { },
                                additionalProperties = false
                            },
                            strict = true
                        }
                    },
                    tool_choice = new
                    {
                        type = "function",
                        name = CompatibilityToolName
                    }
                },
                JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await SendAsync(request, cancellationToken);
        var payload = await ReadPayloadAsync(response, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var responseId = RequiredString(root, "id");
            if (!TryFindFunctionCall(root, out var functionName) ||
                !string.Equals(functionName, CompatibilityToolName, StringComparison.Ordinal))
            {
                throw new QwenApiException(
                    QwenApiErrorKind.IncompatibleModel,
                    "该模型虽然可访问，但未返回 Codex 所需的 Function Call，不能应用为 Codex 模型。");
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

            return new ApiValidationResult
            {
                ResponseId = responseId,
                ToolName = functionName,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens
            };
        }
        catch (QwenApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidResponse("千问 Responses 响应不是有效 JSON。");
        }
        catch (InvalidOperationException)
        {
            throw InvalidResponse("千问 Responses 响应格式无效。");
        }
    }

    private static Uri CreateBaseUrl(QwenRegion region, string? workspaceId) =>
        QwenRegions.Get(region).CreateBaseUrl(workspaceId);

    private async Task<HttpResponseMessage> SendModelListAsync(
        HttpRequestMessage request, Uri baseUrl, int page, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(request, cancellationToken);
        }
        catch (QwenApiException exception) when (exception.StatusCode == 404 && page == 1 &&
            request.RequestUri!.AbsolutePath == "/api/v1/models")
        {
            // Older regional deployments still expose the compatible-mode catalog.
            using var fallback = CreateRequest(HttpMethod.Get, new Uri(baseUrl, "models"), apiKey);
            return await SendAsync(fallback, cancellationToken);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string apiKey)
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

            var status = response.StatusCode;
            response.Dispose();
            throw CreateHttpFailure(status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QwenApiException(QwenApiErrorKind.Timeout, "千问请求超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (QwenApiException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TimeoutException)
        {
            throw new QwenApiException(
                QwenApiErrorKind.Network,
                "无法连接千问 API，请检查网络、地域和业务空间 ID 后重试。");
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
            throw new QwenApiException(QwenApiErrorKind.Timeout, "千问请求超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new QwenApiException(QwenApiErrorKind.Network, "读取千问 API 响应失败。");
        }
    }

    private static QwenApiException CreateHttpFailure(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new QwenApiException(
                QwenApiErrorKind.AuthenticationRequired,
                "千问 API Key 无效、无权访问该地域，或业务空间 ID 不匹配。",
                numeric),
            HttpStatusCode.PaymentRequired => new QwenApiException(
                QwenApiErrorKind.PaymentRequired,
                "阿里云百炼账户余额不足。",
                numeric),
            HttpStatusCode.TooManyRequests => new QwenApiException(
                QwenApiErrorKind.RateLimited,
                "千问请求过于频繁，请稍后重试。",
                numeric),
            _ => new QwenApiException(
                QwenApiErrorKind.RemoteService,
                $"千问 API 请求失败（HTTP {numeric}）。",
                numeric)
        };
    }

    private static string ReadModelId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            id.GetString() is { Length: > 0 } value)
        {
            return value.Trim();
        }

        throw InvalidResponse("千问模型列表包含无效模型条目。");
    }

    private static bool TryFindFunctionCall(JsonElement root, out string functionName)
    {
        functionName = string.Empty;
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "function_call", StringComparison.Ordinal) &&
                item.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } value)
            {
                functionName = value;
                return true;
            }
        }

        return false;
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        throw InvalidResponse($"千问响应缺少字段 {propertyName}。");
    }

    private static int? OptionalInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        throw InvalidResponse($"千问响应字段 {propertyName} 不是有效整数。");
    }

    private static void ValidateApiKey(string apiKey)
    {
        var normalized = apiKey?.Trim() ?? string.Empty;
        if (normalized.Length < 4 ||
            !normalized.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("sk-sp-", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "仅支持阿里云百炼按量付费 API Key；Token Plan 和 Coding Plan 专用 Key 不受支持。",
                nameof(apiKey));
        }
    }

    private static void ValidateModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) ||
            !model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ||
            model.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new ArgumentException("千问模型 ID 格式无效。", nameof(model));
        }
    }

    private static QwenApiException InvalidResponse(string message) =>
        new(QwenApiErrorKind.InvalidResponse, message);
}
