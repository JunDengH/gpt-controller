using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class AccountMetadataServiceTests
{
    private readonly AccountMetadataService _service = new();

    [TestMethod]
    [DataRow("free", MembershipPlan.Free)]
    [DataRow("guest", MembershipPlan.Free)]
    [DataRow("plus", MembershipPlan.Plus)]
    [DataRow("prolite", MembershipPlan.Pro5x)]
    [DataRow("pro_lite", MembershipPlan.Pro5x)]
    [DataRow("pro-lite", MembershipPlan.Pro5x)]
    [DataRow("pro lite", MembershipPlan.Pro5x)]
    [DataRow("pro", MembershipPlan.Pro20x)]
    [DataRow("team", MembershipPlan.Team)]
    [DataRow("business", MembershipPlan.Business)]
    [DataRow("future-plan", MembershipPlan.Unknown)]
    public void NormalizePlan_UsesExpectedMappings(string raw, MembershipPlan expected)
    {
        Assert.AreEqual(expected, _service.NormalizePlan(raw));
    }

    [TestMethod]
    public void Resolve_PrefersQuotaPlanOverAccountAndJwt()
    {
        var claims = Claims("free");
        var resolved = _service.Resolve(
            claims,
            quotaPlanType: "pro",
            accountRead: new AccountReadMetadata("read@example.com", "plus"));

        Assert.AreEqual(MembershipPlan.Pro20x, resolved.MembershipPlan);
        Assert.AreEqual("read@example.com", resolved.Email);
    }

    [TestMethod]
    public void Resolve_NonBusinessPlanIsAlwaysPersonal()
    {
        var claims = new AuthClaims(
            "user@example.com",
            "account-1",
            "org-1",
            "plus",
            [new OrganizationClaim("org-1", "Example Corp")],
            null);

        var resolved = _service.Resolve(claims);

        Assert.AreEqual(AccountOwnershipKind.Personal, resolved.Ownership.Kind);
        Assert.AreEqual("个人账号", resolved.Ownership.DisplayName);
    }

    [TestMethod]
    public void Resolve_BusinessMatchesCurrentOrganization()
    {
        var claims = new AuthClaims(
            "user@example.com",
            "account-1",
            "org-2",
            "business",
            [
                new OrganizationClaim("org-1", "Wrong Corp"),
                new OrganizationClaim("org-2", "Example Corp")
            ],
            null);

        var resolved = _service.Resolve(claims);

        Assert.AreEqual(AccountOwnershipKind.Organization, resolved.Ownership.Kind);
        Assert.AreEqual("org-2", resolved.Ownership.OrganizationId);
        Assert.AreEqual("Example Corp", resolved.Ownership.DisplayName);
    }

    [TestMethod]
    public void Resolve_TeamUsesOnlyOrganizationAsFallback()
    {
        var claims = new AuthClaims(
            "user@example.com",
            "account-1",
            null,
            "team",
            [new OrganizationClaim("org-9", "Only Corp")],
            null);

        var resolved = _service.Resolve(claims);

        Assert.AreEqual("Only Corp", resolved.Ownership.DisplayName);
    }

    [TestMethod]
    public void Resolve_BusinessWithAmbiguousOrganizationsDoesNotGuess()
    {
        var claims = new AuthClaims(
            "user@example.com",
            "account-1",
            null,
            "business",
            [
                new OrganizationClaim("org-1", "First"),
                new OrganizationClaim("org-2", "Second")
            ],
            null);

        var resolved = _service.Resolve(claims);

        Assert.AreEqual("企业账号（名称未知）", resolved.Ownership.DisplayName);
    }

    private static AuthClaims Claims(string plan) =>
        new(
            "jwt@example.com",
            "account-1",
            null,
            plan,
            [],
            null);
}
