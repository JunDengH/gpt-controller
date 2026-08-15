using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class AccountMetadataServiceTests
{
    private readonly AccountMetadataService _service = new();

    [Theory]
    [InlineData("team", MembershipPlan.Team)]
    [InlineData("chatgpt_team", MembershipPlan.Team)]
    [InlineData("team_business", MembershipPlan.Business)]
    [InlineData("team business", MembershipPlan.Business)]
    [InlineData("teambusiness", MembershipPlan.Business)]
    [InlineData("chatgpt_team_business", MembershipPlan.Business)]
    [InlineData("business", MembershipPlan.Business)]
    [InlineData("chatgpt_business", MembershipPlan.Business)]
    [InlineData("self_serve_business", MembershipPlan.Business)]
    [InlineData("self_serve_business_prolite", MembershipPlan.Business)]
    [InlineData("self_serve_business_usage_based", MembershipPlan.Business)]
    [InlineData("enterprise", MembershipPlan.Enterprise)]
    [InlineData("chatgpt_enterprise", MembershipPlan.Enterprise)]
    [InlineData("enterprise_cbp_automation", MembershipPlan.Enterprise)]
    [InlineData("enterprise_cbp_usage_based", MembershipPlan.Enterprise)]
    [InlineData("ent26", MembershipPlan.Enterprise)]
    [InlineData("hc", MembershipPlan.Enterprise)]
    [InlineData("education", MembershipPlan.Edu)]
    [InlineData("edu", MembershipPlan.Edu)]
    [InlineData("chatgpt_edu", MembershipPlan.Edu)]
    public void WorkspacePlansAreRecognized(
        string rawPlan,
        MembershipPlan expected)
    {
        Assert.Equal(expected, _service.NormalizePlan(rawPlan));
    }

    [Theory]
    [InlineData("future-plan", "business", "team", MembershipPlan.Business)]
    [InlineData("future-plan", "unknown-plan", "team", MembershipPlan.Team)]
    public void UnknownPlanSignalsDoNotHideLaterRecognizedWorkspacePlans(
        string quotaPlan,
        string accountReadPlan,
        string claimsPlan,
        MembershipPlan expected)
    {
        var metadata = _service.Resolve(
            Claims("workspace-current", planType: claimsPlan),
            quotaPlan,
            new AccountReadMetadata(
                "test@example.com",
                accountReadPlan,
                "workspace-current"));

        Assert.Equal(expected, metadata.MembershipPlan);
        Assert.Equal(AccountOwnershipKind.Organization, metadata.Ownership.Kind);
    }

    [Fact]
    public void ExistingSerializedMembershipPlanValuesRemainStable()
    {
        Assert.Equal(0, (int)MembershipPlan.Unknown);
        Assert.Equal(1, (int)MembershipPlan.Free);
        Assert.Equal(2, (int)MembershipPlan.Plus);
        Assert.Equal(3, (int)MembershipPlan.Pro5x);
        Assert.Equal(4, (int)MembershipPlan.Pro20x);
        Assert.Equal(5, (int)MembershipPlan.Team);
        Assert.Equal(6, (int)MembershipPlan.Business);
        Assert.Equal(7, (int)MembershipPlan.Enterprise);
        Assert.Equal(8, (int)MembershipPlan.Edu);
        Assert.Equal(0, (int)AccountOwnershipKind.Personal);
        Assert.Equal(1, (int)AccountOwnershipKind.Organization);
    }

    [Fact]
    public void GoPlanIsNotClassifiedAsAWorkspace()
    {
        var metadata = _service.Resolve(Claims(
            accountId: "personal-account",
            planType: "go"));

        Assert.Equal(MembershipPlan.Unknown, metadata.MembershipPlan);
        Assert.Equal(AccountOwnershipKind.Personal, metadata.Ownership.Kind);
    }

    [Fact]
    public void CurrentWorkspaceAccountIdWinsAcrossMultipleOrganizations()
    {
        var claims = Claims(
            accountId: "workspace-current",
            organizationId: "workspace-other",
            planType: "business",
            organizations:
            [
                new OrganizationClaim("workspace-other", "其他组织"),
                new OrganizationClaim("workspace-current", "当前组织")
            ]);

        var metadata = _service.Resolve(claims);

        Assert.Equal(AccountOwnershipKind.Organization, metadata.Ownership.Kind);
        Assert.Equal("workspace-current", metadata.Ownership.OrganizationId);
        Assert.Equal("当前组织", metadata.Ownership.DisplayName);
    }

    [Fact]
    public void UnmatchedOrganizationIsNotGuessedFromAWorkspaceList()
    {
        var claims = Claims(
            accountId: "workspace-current",
            organizationId: "workspace-other",
            planType: "business",
            organizations:
            [
                new OrganizationClaim("workspace-other", "不应误用")
            ]);

        var metadata = _service.Resolve(claims);

        Assert.Equal(AccountOwnershipKind.Organization, metadata.Ownership.Kind);
        Assert.Equal(
            AccountOwnership.UnknownOrganizationDisplayName,
            metadata.Ownership.DisplayName);
    }

    [Theory]
    [InlineData("Personal")]
    [InlineData("Personal account")]
    [InlineData("个人账号")]
    [InlineData("  pErSoNaL AcCoUnT  ")]
    public void LegacyPersonalLabelsAreNotOrganizationNames(string displayName)
    {
        Assert.False(AccountOwnership.HasKnownOrganizationName(displayName));
        Assert.Equal(
            AccountOwnership.UnknownOrganizationDisplayName,
            AccountOwnership.Organization("workspace-current", displayName).DisplayName);
    }

    [Fact]
    public void SelectedAppServerWorkspaceIsAuthoritative()
    {
        var claims = Claims(
            accountId: "workspace-current",
            organizationId: "workspace-current",
            planType: "business",
            organizations:
            [
                new OrganizationClaim("workspace-current", "过期名称")
            ]);
        var accountRead = new AccountReadMetadata(
            "current@example.com",
            "business",
            "workspace-current",
            AccountWorkspaceKind.Workspace,
            "最新名称");

        var metadata = _service.Resolve(claims, accountRead: accountRead);

        Assert.Equal("workspace-current", metadata.AccountId);
        Assert.Equal("最新名称", metadata.Ownership.DisplayName);
    }

    [Fact]
    public void SparseRefreshPreservesKnownWorkspaceNameForSameAccount()
    {
        var cached = Profile(
            "workspace-current",
            MembershipPlan.Business,
            AccountOwnership.Organization("workspace-current", "已缓存组织"));
        var claims = Claims(accountId: "workspace-current");

        var metadata = _service.Resolve(claims, cached: cached);

        Assert.Equal(MembershipPlan.Business, metadata.MembershipPlan);
        Assert.Equal(AccountOwnershipKind.Organization, metadata.Ownership.Kind);
        Assert.Equal("已缓存组织", metadata.Ownership.DisplayName);
    }

    [Fact]
    public void CachedWorkspaceNameIsNeverBorrowedAcrossAccounts()
    {
        var cached = Profile(
            "workspace-other",
            MembershipPlan.Business,
            AccountOwnership.Organization("workspace-other", "其他组织"));
        var claims = Claims(
            accountId: "workspace-current",
            planType: "business");

        var metadata = _service.Resolve(claims, cached: cached);

        Assert.Equal(
            AccountOwnership.UnknownOrganizationDisplayName,
            metadata.Ownership.DisplayName);
    }

    [Fact]
    public void CachedOrganizationCanUseAnIdDistinctFromWorkspaceAccountId()
    {
        var cached = Profile(
            "workspace-current",
            MembershipPlan.Business,
            AccountOwnership.Organization("organization-distinct", "已缓存组织"));
        var claims = Claims(accountId: "workspace-current");

        var metadata = _service.Resolve(claims, cached: cached);

        Assert.Equal(AccountOwnershipKind.Organization, metadata.Ownership.Kind);
        Assert.Equal("organization-distinct", metadata.Ownership.OrganizationId);
        Assert.Equal("已缓存组织", metadata.Ownership.DisplayName);
    }

    [Fact]
    public void SelectedWorkspaceRefreshPreservesKnownDistinctOrganizationId()
    {
        var cached = Profile(
            "workspace-current",
            MembershipPlan.Business,
            AccountOwnership.Organization("organization-distinct", "旧组织名"));
        var accountRead = new AccountReadMetadata(
            "test@example.com",
            "business",
            "workspace-current",
            AccountWorkspaceKind.Workspace,
            "最新组织名");

        var metadata = _service.Resolve(
            Claims(accountId: "workspace-current"),
            accountRead: accountRead,
            cached: cached);

        Assert.Equal("organization-distinct", metadata.Ownership.OrganizationId);
        Assert.Equal("最新组织名", metadata.Ownership.DisplayName);
    }

    [Theory]
    [InlineData("Personal")]
    [InlineData("Personal account")]
    [InlineData("个人账号")]
    [InlineData(AccountOwnership.UnknownOrganizationDisplayName)]
    [InlineData("企业账号（名称未知）")]
    public void InvalidOrganizationNameAndIdAreHealedFromCache(string displayName)
    {
        var cached = Profile(
            "workspace-current",
            MembershipPlan.Business,
            new AccountOwnership(
                AccountOwnershipKind.Organization,
                "organization-dirty",
                displayName));
        var claims = Claims(accountId: "workspace-current");

        var metadata = _service.Resolve(claims, cached: cached);

        Assert.Equal(MembershipPlan.Business, metadata.MembershipPlan);
        Assert.Equal(AccountOwnershipKind.Organization, metadata.Ownership.Kind);
        Assert.Equal("workspace-current", metadata.Ownership.OrganizationId);
        Assert.Equal(
            AccountOwnership.UnknownOrganizationDisplayName,
            metadata.Ownership.DisplayName);
    }

    [Fact]
    public void EmailAndPlanCacheAreNeverBorrowedAcrossAccounts()
    {
        var cached = new AccountProfile
        {
            Nickname = "Cached",
            Email = "cached-a@example.com",
            AccountId = "workspace-a",
            MembershipPlan = MembershipPlan.Business,
            Ownership = AccountOwnership.Organization("workspace-a", "组织 A")
        };
        var claims = new AuthClaims(
            null,
            "workspace-b",
            null,
            null,
            [],
            null,
            null);
        var accountRead = new AccountReadMetadata(
            null,
            null,
            "workspace-b");

        var metadata = _service.Resolve(
            claims,
            accountRead: accountRead,
            cached: cached);

        Assert.Equal("未知邮箱", metadata.Email);
        Assert.Equal(MembershipPlan.Unknown, metadata.MembershipPlan);
        Assert.Equal(AccountOwnershipKind.Personal, metadata.Ownership.Kind);
    }

    [Fact]
    public void ExplicitPersonalWorkspaceKindOverridesPlanInference()
    {
        var claims = Claims(
            accountId: "personal-account",
            planType: "business");
        var accountRead = new AccountReadMetadata(
            "personal@example.com",
            "business",
            "personal-account",
            AccountWorkspaceKind.Personal);

        var metadata = _service.Resolve(claims, accountRead: accountRead);

        Assert.Equal(AccountOwnershipKind.Personal, metadata.Ownership.Kind);
        Assert.Null(metadata.Ownership.DisplayName);
        Assert.Equal(string.Empty, Profile(
            "personal-account",
            MembershipPlan.Plus,
            metadata.Ownership).OwnershipDisplayName);
    }

    [Theory]
    [InlineData(MembershipPlan.Unknown)]
    [InlineData(MembershipPlan.Free)]
    [InlineData(MembershipPlan.Plus)]
    [InlineData(MembershipPlan.Pro5x)]
    [InlineData(MembershipPlan.Pro20x)]
    public void AccountProfileOnlyDisplaysOrganizationForWorkspacePlans(
        MembershipPlan plan)
    {
        var profile = Profile(
            "personal-account",
            plan,
            AccountOwnership.Organization("organization-distinct", "不应显示"));

        Assert.Equal(string.Empty, profile.OwnershipDisplayName);
    }

    private static AuthClaims Claims(
        string? accountId,
        string? organizationId = null,
        string? planType = null,
        IReadOnlyList<OrganizationClaim>? organizations = null) =>
        new(
            "test@example.com",
            accountId,
            organizationId,
            planType,
            organizations ?? [],
            null,
            null);

    private static AccountProfile Profile(
        string accountId,
        MembershipPlan plan,
        AccountOwnership ownership) =>
        new()
        {
            Nickname = "Test",
            Email = "test@example.com",
            AccountId = accountId,
            MembershipPlan = plan,
            Ownership = ownership
        };
}
