namespace GptController.Models;

public sealed record QuotaSnapshot
{
    public double? FiveHourRemainingPercent { get; init; }
    public double? FiveHourUsedPercent { get; init; }
    public long? FiveHourWindowDurationMinutes { get; init; }
    public DateTimeOffset? FiveHourResetsAt { get; init; }

    // These original fields continue to represent the weekly window so existing
    // local profile indexes can be deserialized without a migration.
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
