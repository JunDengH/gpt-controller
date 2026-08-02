using GptAccountManager.Infrastructure;

namespace GptAccountManager.Tests;

public sealed class RedactingLoggerTests
{
    [Fact]
    public void RedactRemovesDeepSeekApiKeys()
    {
        const string key = "sk-exampleDeepSeekSecret123456789";

        var redacted = RedactingLogger.Redact($"request failed for {key}");

        Assert.DoesNotContain(key, redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted-api-key>", redacted, StringComparison.Ordinal);
    }
}
