using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class QuotaParserTests
{
    private readonly QuotaParser _parser = new();

    [TestMethod]
    public void Parse_SelectsCodexWeeklyBucket()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "planType": "free",
                "primary": { "usedPercent": 99, "windowDurationMins": 10080 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "planType": "pro",
                  "primary": { "usedPercent": 20, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 37.5, "windowDurationMins": 10080, "resetsAt": 1800000000 }
                }
              }
            }
            """);

        var result = _parser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.AreEqual(QuotaStatus.Fresh, result.Snapshot.Status);
        Assert.AreEqual(62.5, result.Snapshot.RemainingPercent);
        Assert.AreEqual(10_080L, result.Snapshot.WindowDurationMinutes);
        Assert.AreEqual("pro", result.PlanType);
        Assert.IsNotNull(result.Snapshot.ResetsAt);
    }

    [TestMethod]
    public void Parse_FallsBackToLongestLongWindow()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "primary": { "usedPercent": 10, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 45, "windowDurationMins": 9000 }
              }
            }
            """);

        var result = _parser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.AreEqual(55d, result.Snapshot.RemainingPercent);
        Assert.AreEqual(9000L, result.Snapshot.WindowDurationMinutes);
    }

    [TestMethod]
    public void Parse_ClampsPercentages()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "secondary": { "usedPercent": 145, "windowDurationMins": 10080 }
              }
            }
            """);

        var result = _parser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.AreEqual(100d, result.Snapshot.UsedPercent);
        Assert.AreEqual(0d, result.Snapshot.RemainingPercent);
    }

    [TestMethod]
    public void Parse_MissingWeeklyWindowIsUnavailable()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "primary": { "usedPercent": 10, "windowDurationMins": 300 }
              }
            }
            """);

        var result = _parser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.AreEqual(QuotaStatus.Unavailable, result.Snapshot.Status);
        Assert.AreEqual("weekly_window_missing", result.Snapshot.ErrorCode);
    }
}
