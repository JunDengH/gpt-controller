namespace GptAccountManager.Models;

public sealed record QuotaSnapshot
{
    public double? RemainingPercent { get; init; }
    public double? UsedPercent { get; init; }
    public long? WindowDurationMinutes { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public QuotaStatus Status { get; init; } = QuotaStatus.Unavailable;
    public string? ErrorCode { get; init; }

    public static QuotaSnapshot Unavailable(string? errorCode = null) =>
        new()
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Status = QuotaStatus.Unavailable,
            ErrorCode = errorCode
        };
}
