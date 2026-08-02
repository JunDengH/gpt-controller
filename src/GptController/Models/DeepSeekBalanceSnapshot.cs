namespace GptController.Models;

public sealed record DeepSeekBalanceSnapshot
{
    public bool IsAvailable { get; init; }
    public IReadOnlyList<DeepSeekBalanceInfo> Balances { get; init; } = [];
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DeepSeekBalanceInfo
{
    public required string Currency { get; init; }
    public decimal TotalBalance { get; init; }
    public decimal GrantedBalance { get; init; }
    public decimal ToppedUpBalance { get; init; }
}
