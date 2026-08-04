using GptController.Models;
using GptController.ViewModels;
using System.Text.Json;

namespace GptController.Tests;

public sealed class AccountCardViewModelTests
{
    [Fact]
    public void DeepSeekCardUsesProviderSpecificStatusLabels()
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
        Assert.Equal("API 可用", card.QuotaStatusText);
    }

    [Fact]
    public void DeepSeekCardExposesGenericApiPresentation()
    {
        var validatedAt = new DateTimeOffset(
            2026,
            8,
            3,
            9,
            30,
            0,
            TimeSpan.Zero);
        var card = new AccountCardViewModel(new DeepSeekConnection
        {
            Nickname = "DeepSeek V4",
            Model = "deepseek-v4-flash",
            KeyLastFour = "1234",
            Status = DeepSeekConnectionStatus.Available,
            LastValidatedAt = validatedAt,
            CnyBalance = 12.5m,
            UsdBalance = 2m
        });

        Assert.Equal(ConnectionCardKind.ApiProvider, card.CardKind);
        var presentation = Assert.IsType<ApiConnectionCardPresentation>(
            card.ApiPresentation);
        Assert.Equal("DeepSeek API", presentation.ProviderName);
        Assert.Equal("deepseek-v4-flash", presentation.Model);
        Assert.Equal("Responses API", presentation.ProtocolDisplayName);
        Assert.Equal("api.deepseek.com", presentation.EndpointHost);
        Assert.Equal(ConnectionDisplayHealth.Healthy, presentation.Health);
        Assert.Equal("API 可用", presentation.StatusText);
        Assert.Equal(validatedAt, presentation.LastValidatedAt);
        Assert.NotEqual("尚未验证", presentation.LastValidatedText);
        Assert.Equal("CNY", presentation.PrimaryMetric.Label);
        Assert.Equal("¥12.50", presentation.PrimaryMetric.ValueText);
        Assert.Equal("人民币余额", presentation.PrimaryMetric.DetailText);
        var metric = Assert.Single(presentation.Metrics);
        Assert.Same(presentation.PrimaryMetric, metric);
    }

    [Fact]
    public void OAuthCardDoesNotExposeApiPresentation()
    {
        var card = CreateCard(QuotaStatus.Fresh, null);

        Assert.Equal(ConnectionCardKind.OAuthAccount, card.CardKind);
        Assert.Null(card.ApiPresentation);
    }

    [Theory]
    [InlineData(DeepSeekConnectionStatus.Unknown, ConnectionDisplayHealth.Unknown, "尚未验证")]
    [InlineData(DeepSeekConnectionStatus.AuthenticationRequired, ConnectionDisplayHealth.AuthenticationRequired, "认证无效")]
    [InlineData(DeepSeekConnectionStatus.PaymentRequired, ConnectionDisplayHealth.PaymentRequired, "余额不足")]
    [InlineData(DeepSeekConnectionStatus.RateLimited, ConnectionDisplayHealth.RateLimited, "请求受限")]
    [InlineData(DeepSeekConnectionStatus.Stale, ConnectionDisplayHealth.Stale, "显示上次数据")]
    [InlineData(DeepSeekConnectionStatus.Unavailable, ConnectionDisplayHealth.Unavailable, "暂不可用")]
    public void DeepSeekStatusMapsToGenericDisplayHealth(
        DeepSeekConnectionStatus status,
        ConnectionDisplayHealth expectedHealth,
        string expectedText)
    {
        var card = new AccountCardViewModel(new DeepSeekConnection
        {
            Status = status
        });

        Assert.Equal(expectedHealth, card.ApiPresentation?.Health);
        Assert.Equal(expectedText, card.ApiPresentation?.StatusText);
    }

    [Fact]
    public void ApiPresentationHandlesMissingBalanceAndValidation()
    {
        var card = new AccountCardViewModel(new DeepSeekConnection());

        var presentation = Assert.IsType<ApiConnectionCardPresentation>(
            card.ApiPresentation);
        Assert.Equal("尚未验证", presentation.LastValidatedText);
        Assert.All(
            presentation.Metrics,
            metric => Assert.Equal("—", metric.ValueText));
    }

    [Fact]
    public void ApiPresentationContractNeverExposesCredentialOrUsdBalance()
    {
        const string fullCredential = "sk-this-value-must-never-be-rendered-9876";
        var card = new AccountCardViewModel(new DeepSeekConnection
        {
            KeyLastFour = fullCredential,
            CnyBalance = 8.5m,
            UsdBalance = 999m
        });

        var presentation = Assert.IsType<ApiConnectionCardPresentation>(
            card.ApiPresentation);
        var propertyNames = typeof(ApiConnectionCardPresentation)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));

        var serialized = JsonSerializer.Serialize(presentation);
        Assert.DoesNotContain(fullCredential, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("9876", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999", serialized, StringComparison.Ordinal);
        Assert.Single(presentation.Metrics);
        Assert.Equal("CNY", presentation.PrimaryMetric.Label);
    }

    [Fact]
    public void UpdatingDeepSeekRaisesApiPresentationNotifications()
    {
        var card = new AccountCardViewModel(new DeepSeekConnection());
        var changedProperties = new List<string?>();
        card.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        card.UpdateDeepSeek(new DeepSeekConnection
        {
            KeyLastFour = "4321",
            Status = DeepSeekConnectionStatus.Available
        });

        Assert.Contains(nameof(AccountCardViewModel.CardKind), changedProperties);
        Assert.Contains(nameof(AccountCardViewModel.ApiPresentation), changedProperties);
        Assert.Equal(
            ConnectionDisplayHealth.Healthy,
            card.ApiPresentation?.Health);
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
