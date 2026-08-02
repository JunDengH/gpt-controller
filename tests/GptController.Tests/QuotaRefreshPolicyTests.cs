using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class QuotaRefreshPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(301, true)]
    [InlineData(300, true)]
    [InlineData(299, false)]
    public void ActiveAccountProbeUsesFiveMinuteSafetyWindow(
        int secondsRemaining,
        bool expected)
    {
        var expiration = Now.AddSeconds(secondsRemaining);

        Assert.Equal(
            expected,
            QuotaRefreshPolicy.CanProbeActiveAccount(expiration, Now));
    }

    [Fact]
    public void ActiveAccountProbeIsDeferredWhenExpirationCannotBeRead()
    {
        Assert.False(QuotaRefreshPolicy.CanProbeActiveAccount(null, Now));
    }

    [Theory]
    [InlineData("invalid_auth", true)]
    [InlineData("confirmed_unauthorized", true)]
    [InlineData("authentication_required", false)]
    [InlineData("quota_refresh_failed", false)]
    public void AutomaticRefreshOnlySkipsConfirmedAuthenticationErrors(
        string errorCode,
        bool expected)
    {
        var quota = new QuotaSnapshot
        {
            FetchedAt = Now,
            Status = QuotaStatus.AuthenticationRequired,
            ErrorCode = errorCode
        };

        Assert.Equal(expected, QuotaRefreshPolicy.ShouldSkipAutomatic(quota));
    }

    [Fact]
    public void StructuredUnauthorizedResponseIsConfirmedAuthenticationFailure()
    {
        var exception = new CodexAppServerException(
            "account/read",
            "401",
            "Request rejected.");

        Assert.True(QuotaRefreshPolicy.IsConfirmedAuthenticationFailure(exception));
    }

    [Theory]
    [InlineData("No signed-in account is available.")]
    [InlineData("The request timed out.")]
    [InlineData("Service unavailable (503).")]
    [InlineData("Unexpected protocol response.")]
    public void AmbiguousRemoteFailuresDoNotRequireRelogin(string message)
    {
        Assert.False(
            QuotaRefreshPolicy.IsConfirmedAuthenticationFailure(
                new InvalidOperationException(message)));
    }

    [Fact]
    public void TimeoutIsNotRetriedImmediately()
    {
        Assert.False(
            QuotaRefreshPolicy.IsFastTransientFailure(
                new TimeoutException("The request timed out.")));
    }

    [Theory]
    [InlineData("Request failed with 429.")]
    [InlineData("Request failed with 503.")]
    [InlineData("Connection reset by peer.")]
    public void FastTransientFailuresCanBeRetriedOnce(string message)
    {
        Assert.True(
            QuotaRefreshPolicy.IsFastTransientFailure(
                new InvalidOperationException(message)));
    }

    [Fact]
    public void SlowTransientFailureIsDeferredToTheNextScheduledRefresh()
    {
        Assert.False(
            QuotaRefreshPolicy.ShouldRetryFastTransient(
                new InvalidOperationException("Request failed with 503."),
                TimeSpan.FromSeconds(3)));
    }
}
