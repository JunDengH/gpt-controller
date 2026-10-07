using System.Text.Json.Serialization;

namespace GptController.Models;

public sealed record DeepSeekConnection
{
    public const string FixedId = "deepseek";

    public string Id { get; init; } = FixedId;
    public string Nickname { get; init; } = "DeepSeek";
    public string Model { get; init; } = DeepSeekDefaults.Model;
    public string KeyLastFour { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidatedAt { get; init; }
    public bool? IsAvailable { get; init; }
    public decimal? CnyBalance { get; init; }
    public decimal? UsdBalance { get; init; }
    public DeepSeekConnectionStatus Status { get; init; } =
        DeepSeekConnectionStatus.Unknown;
    public string? ErrorCode { get; init; }
    public DeepSeekBalanceSnapshot? Balance { get; init; }
    public IReadOnlyList<ApiModelDescriptor> Models { get; init; } = [];
    public DateTimeOffset? ModelsFetchedAt { get; init; }
    public bool IsModelCacheStale { get; init; }

    [JsonIgnore]
    public string MaskedApiKey => string.IsNullOrWhiteSpace(KeyLastFour)
        ? "未配置"
        : $"•••• {KeyLastFour}";
}

public enum DeepSeekConnectionStatus
{
    Unknown,
    Available,
    Unavailable,
    AuthenticationRequired,
    PaymentRequired,
    RateLimited,
    Stale
}

public static class DeepSeekDefaults
{
    public const string BaseUrl = "https://api.deepseek.com/";
    public const string FlashModel = "deepseek-flash";
    public const string ProModel = "deepseek-v4-pro";
    public const string Model = FlashModel;

    public static bool IsSupportedModel(string? model) =>
        model is { Length: > 9 and <= 200 } &&
        model.StartsWith("deepseek-", StringComparison.Ordinal) &&
        model.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static string GetModelDisplayName(string model) => model switch
    {
        FlashModel => "DeepSeek Flash",
        ProModel => "DeepSeek Pro",
        "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp" => "DeepSeek Flash（旧别名）",
        _ => model
    };
}
