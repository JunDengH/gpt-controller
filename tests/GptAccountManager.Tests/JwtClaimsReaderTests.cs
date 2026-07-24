using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class JwtClaimsReaderTests
{
    [TestMethod]
    public void Read_ExtractsAccountPlanAndOrganizations()
    {
        var authBytes = TestAuthFactory.Create(
            "account-42",
            "member@example.com",
            "business",
            organizationId: "org-2",
            organizations:
            [
                ("org-1", "First Corp"),
                ("org-2", "Second Corp")
            ]);

        var claims = JwtClaimsReader.Read(AuthDocument.Inspect(authBytes));

        Assert.AreEqual("account-42", claims.AccountId);
        Assert.AreEqual("member@example.com", claims.Email);
        Assert.AreEqual("business", claims.PlanType);
        Assert.AreEqual("org-2", claims.OrganizationId);
        Assert.AreEqual(2, claims.Organizations.Count);
        Assert.IsTrue(claims.ExpiresAt > DateTimeOffset.UtcNow);
    }
}
