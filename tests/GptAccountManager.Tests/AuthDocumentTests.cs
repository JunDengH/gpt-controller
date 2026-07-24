using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class AuthDocumentTests
{
    [TestMethod]
    public void RemoveRefreshToken_KeepsIdentityAndAccessToken()
    {
        var original = TestAuthFactory.Create("account-1");

        var sanitized = AuthDocument.RemoveRefreshToken(original);
        var info = AuthDocument.Inspect(sanitized);

        Assert.IsTrue(info.HasManagedTokens);
        Assert.IsFalse(info.HasRefreshToken);
        Assert.AreEqual("account-1", JwtClaimsReader.Read(info).AccountId);
    }

    [TestMethod]
    public void SemanticallyEqual_IgnoresFormatting()
    {
        var compact = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":[2]}");
        var formatted = Encoding.UTF8.GetBytes("{\n  \"a\": 1,\n  \"b\": [2]\n}");

        Assert.IsTrue(AuthDocument.SemanticallyEqual(compact, formatted));
    }
}
