using GptAccountManager.Models;
using GptAccountManager.ViewModels;

namespace GptAccountManager.Tests;

public sealed class AccountCardViewModelTests
{
    [Fact]
    public void DeepSeekCardUsesBalanceAndProviderSpecificLabels()
    {
        var card = new AccountCardViewModel(new DeepSeekConnection
        {
            Nickname = "DeepSeek V4",
            KeyLastFour = "1234",
            IsAvailable = true,
            Status = DeepSeekConnectionStatus.Available,
            CnyBalance = 12.5m,
            UsdBalance = 2m
        });

        Assert.True(card.IsDeepSeek);
        Assert.Equal("DeepSeek API", card.ProviderDisplayName);
        Assert.Equal("API Key •••• 1234", card.Email);
        Assert.Equal("¥12.50", card.FiveHourRemainingText);
        Assert.Equal("$2.00", card.WeeklyRemainingText);
        Assert.Equal("API 可用", card.QuotaStatusText);
    }

    [Theory]
    [InlineData("invalid_auth", "需要重新登录")]
    [InlineData("confirmed_unauthorized", "需要重新登录")]
    [InlineData("authentication_required", "等待重新验证")]
    public void AuthenticationStatusUsesCompatibleCardCopy(
        string errorCode,
        string expected)
    {
        var card = CreateCard(QuotaStatus.AuthenticationRequired, errorCode);

        Assert.Equal(expected, card.QuotaStatusText);
    }

    [Fact]
    public void DeferredActiveRefreshIsShownAsStaleInsteadOfRelogin()
    {
        var card = CreateCard(QuotaStatus.Stale, "active_refresh_deferred");

        Assert.Equal("等待 ChatGPT 更新登录状态", card.QuotaStatusText);
    }

    [Fact]
    public void RefreshStateIsObservable()
    {
        var card = CreateCard(QuotaStatus.Fresh, null);
        var changedProperties = new List<string?>();
        card.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        card.IsRefreshing = true;

        Assert.True(card.IsRefreshing);
        Assert.Contains(nameof(AccountCardViewModel.IsRefreshing), changedProperties);
    }

    [Fact]
    public void ExposesFiveHourAndWeeklyQuotaSeparately()
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Test",
            Email = "test@example.com",
            AccountId = "account",
            Ownership = AccountOwnership.Personal,
            Quota = new QuotaSnapshot
            {
                FiveHourRemainingPercent = 82.4,
                FiveHourResetsAt =
                    new DateTimeOffset(2026, 7, 30, 13, 0, 0, TimeSpan.Zero),
                RemainingPercent = 44.6,
                ResetsAt =
                    new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero),
                FetchedAt = DateTimeOffset.UtcNow,
                Status = QuotaStatus.Fresh
            }
        });

        Assert.Equal(82.4, card.FiveHourRemainingValue);
        Assert.Equal("82%", card.FiveHourRemainingText);
        Assert.Equal(44.6, card.WeeklyRemainingValue);
        Assert.Equal("45%", card.WeeklyRemainingText);
        Assert.NotEqual(
            card.FiveHourResetValueText,
            card.WeeklyResetValueText);
    }

    private static AccountCardViewModel CreateCard(
        QuotaStatus status,
        string? errorCode) =>
        new(new AccountProfile
        {
            Nickname = "Test",
            Email = "test@example.com",
            AccountId = "account",
            Ownership = AccountOwnership.Personal,
            Quota = new QuotaSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Status = status,
                ErrorCode = errorCode
            }
        });
}
