using System.Text.Json;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class QuotaParserTests
{
    [Fact]
    public void ParsesFiveHourAndWeeklyWindowsByDuration()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "planType": "plus",
                "primary": {
                  "usedPercent": 18.5,
                  "windowDurationMins": 300,
                  "resetsAt": 1785402000
                },
                "secondary": {
                  "usedPercent": 63,
                  "windowDurationMins": 10080,
                  "resetsAt": 1785800000000
                }
              }
            }
            """);
        var now = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

        var result = new QuotaParser().Parse(document.RootElement, now);

        Assert.Equal("plus", result.PlanType);
        Assert.Equal(81.5, result.Snapshot.FiveHourRemainingPercent);
        Assert.Equal(300, result.Snapshot.FiveHourWindowDurationMinutes);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1785402000),
            result.Snapshot.FiveHourResetsAt);
        Assert.Equal(37, result.Snapshot.RemainingPercent);
        Assert.Equal(10_080, result.Snapshot.WindowDurationMinutes);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1785800000000),
            result.Snapshot.ResetsAt);
    }

    [Fact]
    public void KeepsWeeklyOnlyResponsesCompatible()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimitsByLimitId": {
                "codex": {
                  "planType": "pro",
                  "primary": {
                    "usedPercent": "25",
                    "windowDurationMins": "10080",
                    "resetsAt": "1785200000"
                  }
                }
              }
            }
            """);

        var result = new QuotaParser().Parse(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.Null(result.Snapshot.FiveHourRemainingPercent);
        Assert.Equal(75, result.Snapshot.RemainingPercent);
        Assert.Equal(Models.QuotaStatus.Fresh, result.Snapshot.Status);
    }

    [Fact]
    public void ParsesFiveHourOnlyResponsesWithoutDiscardingThem()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "primary": {
                  "usedPercent": 40,
                  "windowDurationMins": 300
                }
              }
            }
            """);

        var result = new QuotaParser().Parse(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.Equal(60, result.Snapshot.FiveHourRemainingPercent);
        Assert.Null(result.Snapshot.RemainingPercent);
        Assert.Equal(Models.QuotaStatus.Fresh, result.Snapshot.Status);
    }
}
