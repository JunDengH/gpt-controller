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
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<ChatGptConnection> ChatGptConnections { get; init; } = [];
    public DeepSeekConnection? DeepSeekConnection { get; init; }
    public ActiveConnectionRef? ActiveConnection { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
