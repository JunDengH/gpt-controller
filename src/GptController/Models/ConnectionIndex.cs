namespace GptController.Models;

public sealed record ActiveConnectionRef
{
    public required ConnectionProvider Provider { get; init; }
    public required string ConnectionId { get; init; }
}

public sealed record ChatGptConnection
{
    public required string Id { get; init; }
    public required Guid ProfileId { get; init; }
    public required string Nickname { get; init; }
    public required string Email { get; init; }
    public required string AccountId { get; init; }
    public bool IsActive { get; init; }
    public MembershipPlan MembershipPlan { get; init; }
    public AccountOwnership Ownership { get; init; } =
        AccountOwnership.Personal;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ConnectionIndex
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<ChatGptConnection> ChatGptConnections { get; init; } = [];
    public IReadOnlyList<ApiConnection> ApiConnections { get; init; } = [];

    // Retained for one schema generation so v1 files and integrations can be
    // migrated without losing their DeepSeek projection. New logic uses ApiConnections.
    public DeepSeekConnection? DeepSeekConnection { get; init; }
    public ActiveConnectionRef? ActiveConnection { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
