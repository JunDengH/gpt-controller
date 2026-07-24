using GptAccountManager.Infrastructure;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class RedactingLoggerTests
{
    [TestMethod]
    public void Redact_RemovesJwtEmailBearerAndRefreshToken()
    {
        const string input =
            "user@example.com Bearer abc.def.ghi " +
            "\"refresh_token\":\"secret\" eyJabc.def.ghi";

        var result = RedactingLogger.Redact(input);

        Assert.IsFalse(result.Contains("user@example.com"));
        Assert.IsFalse(result.Contains("secret"));
        Assert.IsFalse(result.Contains("eyJabc"));
        Assert.IsFalse(result.Contains("Bearer abc"));
    }
}
