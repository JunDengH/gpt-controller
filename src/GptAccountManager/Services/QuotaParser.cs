using System.Globalization;
using System.Text.Json;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed record QuotaParseResult(QuotaSnapshot Snapshot, string? PlanType);

public sealed class QuotaParser
{
    private const long FiveHourMinutes = 300;
    private const long WeeklyMinutes = 10_080;
    private const long MinimumLongWindowMinutes = 1_440;
    private const double FiveHourTolerance = 0.20;
    private const double WeeklyTolerance = 0.20;

    public QuotaParseResult Parse(JsonElement result, DateTimeOffset now)
    {
        var bucket = SelectBucket(result);
        if (bucket is null)
        {
            return new QuotaParseResult(
                QuotaSnapshot.Unavailable("quota_windows_missing"),
                null);
        }

        var windows = new[] { "primary", "secondary" }
            .Select(name => TryGetObject(bucket.Value, name))
            .Where(value => value.HasValue)
            .Select(value => ParseWindow(value!.Value))
            .Where(value => value is not null)
            .Cast<ParsedWindow>()
            .ToList();

        var fiveHour = windows
            .Where(window =>
                window.DurationMinutes.HasValue &&
                Math.Abs(window.DurationMinutes.Value - FiveHourMinutes) <=
                FiveHourMinutes * FiveHourTolerance)
            .OrderBy(window =>
                Math.Abs(window.DurationMinutes!.Value - FiveHourMinutes))
            .FirstOrDefault();

        var weekly = windows
            .Where(window =>
                window.DurationMinutes.HasValue &&
                Math.Abs(window.DurationMinutes.Value - WeeklyMinutes) <= WeeklyMinutes * WeeklyTolerance)
            .OrderBy(window => Math.Abs(window.DurationMinutes!.Value - WeeklyMinutes))
            .FirstOrDefault()
            ?? windows
                .Where(window => window.DurationMinutes >= MinimumLongWindowMinutes)
                .OrderByDescending(window => window.DurationMinutes)
                .FirstOrDefault();

        if (fiveHour is null && weekly is null)
        {
            return new QuotaParseResult(
                new QuotaSnapshot
                {
                    FetchedAt = now,
                    Status = QuotaStatus.Unavailable,
                    ErrorCode = "quota_windows_missing"
                },
                ReadString(bucket.Value, "planType"));
        }

        var fiveHourUsed = fiveHour is null
            ? (double?)null
            : Math.Clamp(fiveHour.UsedPercent, 0, 100);
        var weeklyUsed = weekly is null
            ? (double?)null
            : Math.Clamp(weekly.UsedPercent, 0, 100);
        return new QuotaParseResult(
            new QuotaSnapshot
            {
                FiveHourUsedPercent = fiveHourUsed,
                FiveHourRemainingPercent = fiveHourUsed.HasValue
                    ? Math.Clamp(100 - fiveHourUsed.Value, 0, 100)
                    : null,
                FiveHourWindowDurationMinutes = fiveHour?.DurationMinutes,
                FiveHourResetsAt = fiveHour?.ResetsAt,
                UsedPercent = weeklyUsed,
                RemainingPercent = weeklyUsed.HasValue
                    ? Math.Clamp(100 - weeklyUsed.Value, 0, 100)
                    : null,
                WindowDurationMinutes = weekly?.DurationMinutes,
                ResetsAt = weekly?.ResetsAt,
                FetchedAt = now,
                Status = QuotaStatus.Fresh
            },
            ReadString(bucket.Value, "planType"));
    }

    private static JsonElement? SelectBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object &&
            byId.TryGetProperty("codex", out var codex) &&
            codex.ValueKind == JsonValueKind.Object)
        {
            return codex;
        }

        if (result.TryGetProperty("rateLimits", out var fallback) &&
            fallback.ValueKind == JsonValueKind.Object)
        {
            return fallback;
        }

        return null;
    }

    private static ParsedWindow? ParseWindow(JsonElement window)
    {
        var used = ReadDouble(window, "usedPercent");
        if (!used.HasValue)
        {
            return null;
        }

        var duration = ReadLong(window, "windowDurationMins");
        var reset = ReadLong(window, "resetsAt");
        DateTimeOffset? resetAt = null;
        if (reset is > 0)
        {
            try
            {
                resetAt = reset > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(reset.Value)
                    : DateTimeOffset.FromUnixTimeSeconds(reset.Value);
            }
            catch
            {
                resetAt = null;
            }
        }

        return new ParsedWindow(used.Value, duration, resetAt);
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   property.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   property.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private sealed record ParsedWindow(
        double UsedPercent,
        long? DurationMinutes,
        DateTimeOffset? ResetsAt);
}
