namespace GptController.Models;

public sealed record ApiConnection
{
    public required string Id { get; init; }
    public required ConnectionProvider Provider { get; init; }
    public required string Model { get; init; }
    public bool IsActive { get; init; }
    public string? Region { get; init; }
    public string? WorkspaceId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ApiModelDescriptor
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsSnapshot { get; init; }
    public int? ContextWindowTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public IReadOnlyList<string> InputModalities { get; init; } = [];
    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];
    public string? DefaultReasoningEffort { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName)
        ? Id
        : DisplayName;
}

public sealed record ApiProviderDefinition
{
    public required ConnectionProvider Provider { get; init; }
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required string CredentialProvider { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string Model { get; init; }
    public IReadOnlyList<ApiModelDescriptor> Models { get; init; } = [];
    public bool SupportsReasoning { get; init; }
    public string? DefaultReasoningEffort { get; init; }
    public bool SupportsParallelToolCalls { get; init; }
    public bool SupportsSearch { get; init; }
    public int ConservativeContextWindow { get; init; } = 32_768;
}

public sealed record ApiValidationResult
{
    public required string ResponseId { get; init; }
    public required string ToolName { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
}
