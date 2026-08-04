namespace GptController.ViewModels;

public enum ConnectionCardKind
{
    OAuthAccount,
    ApiProvider
}

public enum ConnectionDisplayHealth
{
    Unknown,
    Healthy,
    AuthenticationRequired,
    PaymentRequired,
    RateLimited,
    Stale,
    Unavailable
}

public sealed record ApiMetricPresentation(
    string Label,
    string ValueText,
    string DetailText);

public sealed record ApiConnectionCardPresentation
{
    public required string ProviderName { get; init; }
    public required string Model { get; init; }
    public required string ProtocolDisplayName { get; init; }
    public required string EndpointHost { get; init; }
    public required ConnectionDisplayHealth Health { get; init; }
    public required string StatusText { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
    public required string LastValidatedText { get; init; }
    public required ApiMetricPresentation PrimaryMetric { get; init; }
    public IReadOnlyList<ApiMetricPresentation> Metrics { get; init; } = [];
}
