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
    public void QwenCardUsesCompactRegionAndDynamicModelCount()
    {
        var card = new AccountCardViewModel(new QwenConnection
        {
            Model = "qwen3-coder-plus",
            Region = QwenRegion.Beijing,
            WorkspaceId = "workspace-a",
            Status = ApiConnectionStatus.Available,
            Models =
            [
                new ApiModelDescriptor { Id = "qwen3-coder-plus" },
                new ApiModelDescriptor { Id = "qwen3.8-max" }
            ]
        });

        Assert.True(card.IsQwen);
        Assert.True(card.IsApiProvider);
        Assert.Equal("千问 API", card.ProviderDisplayName);
        Assert.Equal(string.Empty, card.Email);
        var presentation = Assert.IsType<ApiConnectionCardPresentation>(card.ApiPresentation);
        Assert.Equal("北京 · 模型", presentation.PrimaryMetric.Label);
        Assert.Equal("2", presentation.PrimaryMetric.ValueText);
        Assert.Equal("qwen3-coder-plus", presentation.Model);
    }

    [Fact]
    public void ActiveApiCanBeDeletedThroughRestoreTransactionButActiveOAuthCannot()
    {
        var api = new AccountCardViewModel(new QwenConnection
        {
            Model = "qwen3-coder-plus",
            Region = QwenRegion.Virginia,
            IsActive = true,
            Models = [new ApiModelDescriptor { Id = "qwen3-coder-plus" }]
        });
        var oauth = new AccountCardViewModel(new AccountProfile
        {
            AccountId = "oauth-account",
            Email = "oauth@example.com",
            Nickname = "OAuth",
            IsActive = true
        });

        Assert.True(api.CanDelete);
        Assert.False(oauth.CanDelete);
    }

    [Fact]
    public void DeepSeekCardExposesSelectableModelState()
    {
        var card = new AccountCardViewModel(new DeepSeekConnection
        {
            Model = DeepSeekDefaults.ProModel
        });

        Assert.True(card.IsProModel);
        Assert.False(card.IsFlashModel);
        Assert.Equal("V4 Pro", card.ApiModelDisplayName);
        Assert.Contains("复杂编码", card.ApiModelDescription);
    }

    [Fact]
    public void OAuthCardDoesNotExposeApiPresentation()
    {
        var card = CreateCard(QuotaStatus.Fresh, null);

        Assert.Equal(ConnectionCardKind.OAuthAccount, card.CardKind);
        Assert.Null(card.ApiPresentation);
    }

    [Theory]
    [InlineData(MembershipPlan.Team)]
    [InlineData(MembershipPlan.Business)]
    [InlineData(MembershipPlan.Enterprise)]
    [InlineData(MembershipPlan.Edu)]
    public void OrganizationPlansDisplayOrganizationName(
        MembershipPlan membershipPlan)
    {
        var organization = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Team",
            Email = "team@example.cn",
            AccountId = "team-account",
            MembershipPlan = membershipPlan,
            Ownership = AccountOwnership.Organization("org", "示例科技")
        });

        Assert.True(organization.IsOrganization);
        Assert.Equal("示例科技", organization.CompanyDisplayName);
        Assert.Equal("示例科技", organization.OwnershipDisplayName);
        Assert.Equal(
            "team@example.cn · 示例科技",
            organization.AccountIdentityDisplayName);
        Assert.Equal(" · ", organization.AccountIdentitySeparator);
    }

    [Theory]
    [InlineData(MembershipPlan.Unknown)]
    [InlineData(MembershipPlan.Free)]
    [InlineData(MembershipPlan.Plus)]
    [InlineData(MembershipPlan.Pro5x)]
    [InlineData(MembershipPlan.Pro20x)]
    public void NonOrganizationPlansOnlyDisplayEmailEvenWithOrganizationMetadata(
        MembershipPlan membershipPlan)
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Personal",
            Email = "personal@example.cn",
            AccountId = "personal-account",
            MembershipPlan = membershipPlan,
            Ownership = AccountOwnership.Organization("stale-org", "错误缓存的组织名")
        });

        Assert.False(card.IsOrganization);
        Assert.Equal(string.Empty, card.CompanyDisplayName);
        Assert.Equal(string.Empty, card.OwnershipDisplayName);
        Assert.Equal("personal@example.cn", card.AccountIdentityDisplayName);
        Assert.Equal(string.Empty, card.AccountIdentitySeparator);
    }

    [Fact]
    public void OrganizationPlanWithPersonalOwnershipOnlyDisplaysEmail()
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Team",
            Email = "team@example.cn",
            AccountId = "team-account",
            MembershipPlan = MembershipPlan.Team,
            Ownership = AccountOwnership.Personal
        });

        Assert.False(card.IsOrganization);
        Assert.Equal(string.Empty, card.CompanyDisplayName);
        Assert.Equal(string.Empty, card.OwnershipDisplayName);
        Assert.Equal("team@example.cn", card.AccountIdentityDisplayName);
        Assert.Equal(string.Empty, card.AccountIdentitySeparator);
    }

    [Fact]
    public void OrganizationWithoutKnownNameUsesUnknownOrganizationCopy()
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Unknown org",
            Email = "unknown@example.cn",
            AccountId = "unknown-org",
            MembershipPlan = MembershipPlan.Business,
            Ownership = AccountOwnership.Organization("org", null)
        });

        Assert.True(card.IsOrganization);
        Assert.Equal("组织名称未知", card.CompanyDisplayName);
        Assert.Equal("组织名称未知", card.OwnershipDisplayName);
        Assert.Equal(
            "unknown@example.cn · 组织名称未知",
            card.AccountIdentityDisplayName);
        Assert.Equal(" · ", card.AccountIdentitySeparator);
    }

    [Theory]
    [InlineData("Personal")]
    [InlineData("Personal account")]
    [InlineData("个人账号")]
    public void PersonalLabelsInOrganizationCacheUseUnknownOrganizationCopy(
        string cachedDisplayName)
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Legacy team",
            Email = "legacy-team@example.cn",
            AccountId = "legacy-team",
            MembershipPlan = MembershipPlan.Team,
            Ownership = new AccountOwnership(
                AccountOwnershipKind.Organization,
                "legacy-org",
                cachedDisplayName)
        });

        Assert.Equal("组织名称未知", card.CompanyDisplayName);
        Assert.Equal(
            "legacy-team@example.cn · 组织名称未知",
            card.AccountIdentityDisplayName);
    }

    [Fact]
    public void OAuthIdentitySeparatorNeverAppearsWithoutBothValues()
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "No email",
            Email = string.Empty,
            AccountId = "personal-account",
            MembershipPlan = MembershipPlan.Plus,
            Ownership = AccountOwnership.Personal
        });

        Assert.Equal(string.Empty, card.AccountIdentitySeparator);
        Assert.Equal(string.Empty, card.AccountIdentityDisplayName);
    }

    [Fact]
    public void LegacyUnknownOrganizationCopyIsNormalizedForDisplay()
    {
        var card = new AccountCardViewModel(new AccountProfile
        {
            Nickname = "Legacy",
            Email = "legacy@example.com",
            AccountId = "legacy-workspace",
            MembershipPlan = MembershipPlan.Enterprise,
            Ownership = new AccountOwnership(
                AccountOwnershipKind.Organization,
                "legacy-workspace",
                "企业账号（名称未知）")
        });

        Assert.Equal("组织名称未知", card.CompanyDisplayName);
        Assert.Equal(
            "legacy@example.com · 组织名称未知",
            card.AccountIdentityDisplayName);
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
            MembershipPlan = MembershipPlan.Plus,
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
            MembershipPlan = MembershipPlan.Plus,
            Ownership = AccountOwnership.Personal,
            Quota = new QuotaSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Status = status,
                ErrorCode = errorCode
            }
        });
}
