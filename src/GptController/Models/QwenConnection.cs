namespace GptController.Models;

public sealed record QwenConnection
{
    public const string FixedId = "qwen";

    public string Id { get; init; } = FixedId;
    public string Model { get; init; } = string.Empty;
    public QwenRegion Region { get; init; } = QwenRegion.Beijing;
    public string? WorkspaceId { get; init; }
    public string KeyLastFour { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidatedAt { get; init; }
    public DateTimeOffset? ModelsFetchedAt { get; init; }
    public bool IsModelCacheStale { get; init; }
    public ApiConnectionStatus Status { get; init; } = ApiConnectionStatus.Unknown;
    public string? ErrorCode { get; init; }
    public IReadOnlyList<ApiModelDescriptor> Models { get; init; } = [];

    public string MaskedApiKey => string.IsNullOrWhiteSpace(KeyLastFour)
        ? "未配置"
        : $"•••• {KeyLastFour}";
}

public enum ApiConnectionStatus
{
    Unknown,
    Available,
    Unavailable,
    AuthenticationRequired,
    PaymentRequired,
    RateLimited,
    Stale
}

public enum QwenRegion
{
    Beijing,
    Singapore,
    Virginia,
    Frankfurt,
    Tokyo
}

public sealed record QwenRegionDefinition(
    QwenRegion Region,
    string DisplayName,
    string HostTemplate,
    bool RequiresWorkspaceId)
{
    public Uri CreateBaseUrl(string? workspaceId)
    {
        var normalizedWorkspace = QwenRegions.NormalizeWorkspaceId(
            workspaceId,
            RequiresWorkspaceId);
        var host = RequiresWorkspaceId
            ? HostTemplate.Replace("{workspace}", normalizedWorkspace, StringComparison.Ordinal)
            : HostTemplate;
        return new Uri($"https://{host}/compatible-mode/v1/", UriKind.Absolute);
    }
}

public static class QwenRegions
{
    private static readonly IReadOnlyDictionary<QwenRegion, QwenRegionDefinition> Definitions =
        new Dictionary<QwenRegion, QwenRegionDefinition>
        {
            [QwenRegion.Beijing] = new(
                QwenRegion.Beijing,
                "华北 2（北京）",
                "{workspace}.cn-beijing.maas.aliyuncs.com",
                true),
            [QwenRegion.Singapore] = new(
                QwenRegion.Singapore,
                "新加坡",
                "{workspace}.ap-southeast-1.maas.aliyuncs.com",
                true),
            [QwenRegion.Virginia] = new(
                QwenRegion.Virginia,
                "美国（弗吉尼亚）",
                "dashscope-us.aliyuncs.com",
                false),
            [QwenRegion.Frankfurt] = new(
                QwenRegion.Frankfurt,
                "德国（法兰克福）",
                "{workspace}.eu-central-1.maas.aliyuncs.com",
                true),
            [QwenRegion.Tokyo] = new(
                QwenRegion.Tokyo,
                "日本（东京）",
                "{workspace}.ap-northeast-1.maas.aliyuncs.com",
                true)
        };

    public static IReadOnlyList<QwenRegionDefinition> All { get; } =
        Definitions.Values.OrderBy(item => item.Region).ToArray();

    public static QwenRegionDefinition Get(QwenRegion region) =>
        Definitions.TryGetValue(region, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(region));

    public static string GetDisplayName(QwenRegion region) => Get(region).DisplayName;

    public static string GetShortDisplayName(QwenRegion region) => region switch
    {
        QwenRegion.Beijing => "北京",
        QwenRegion.Singapore => "新加坡",
        QwenRegion.Virginia => "弗吉尼亚",
        QwenRegion.Frankfurt => "法兰克福",
        QwenRegion.Tokyo => "东京",
        _ => throw new ArgumentOutOfRangeException(nameof(region))
    };

    internal static string NormalizeWorkspaceId(string? workspaceId, bool required)
    {
        var normalized = workspaceId?.Trim() ?? string.Empty;
        if (!required && normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.Length is < 1 or > 63 ||
            normalized[0] == '-' || normalized[^1] == '-' ||
            normalized.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not '-'))
        {
            throw new ArgumentException("业务空间 ID 格式无效。", nameof(workspaceId));
        }

        return normalized.ToLowerInvariant();
    }
}

public static class ApiProviderDefinitions
{
    public const string DeepSeekProviderId = "gpt_controller_deepseek";
    public const string QwenProviderId = "gpt_controller_qwen";

    public static ApiProviderDefinition ForDeepSeek(string model) => new()
    {
        Provider = ConnectionProvider.DeepSeek,
        ProviderId = DeepSeekProviderId,
        DisplayName = "DeepSeek",
        CredentialProvider = "deepseek",
        BaseUrl = new Uri(DeepSeekDefaults.BaseUrl),
        Model = model,
        Models = DeepSeekDefaults.SupportedModels.Select(item => new ApiModelDescriptor
        {
            Id = item,
            DisplayName = DeepSeekDefaults.GetModelDisplayName(item)
        }).ToArray(),
        SupportsReasoning = true,
        SupportsParallelToolCalls = true,
        SupportsSearch = true,
        ConservativeContextWindow = 1_048_576
    };

    public static ApiProviderDefinition ForQwen(QwenConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var models = connection.Models
            .Append(new ApiModelDescriptor { Id = connection.Model })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ApiProviderDefinition
        {
            Provider = ConnectionProvider.Qwen,
            ProviderId = QwenProviderId,
            DisplayName = "Alibaba Cloud Model Studio",
            CredentialProvider = "qwen",
            BaseUrl = QwenRegions.Get(connection.Region).CreateBaseUrl(connection.WorkspaceId),
            Model = connection.Model,
            Models = models,
            SupportsReasoning = false,
            SupportsParallelToolCalls = false,
            SupportsSearch = false,
            ConservativeContextWindow = 32_768
        };
    }
}
