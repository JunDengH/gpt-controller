using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class RetryPolicyTests
{
    [TestMethod]
    public async Task ExecuteAsync_RetriesTransientFailuresWithBackoff()
    {
        var attempts = 0;

        var result = await RetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<int>(new TimeoutException())
                    : Task.FromResult(42);
            },
            exception => exception is TimeoutException,
            maxAttempts: 3,
            initialDelay: TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(42, result);
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ExecuteAsync_DoesNotRetryPermanentFailure()
    {
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RetryPolicy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;
                    return Task.FromException<int>(new InvalidDataException());
                },
                exception => exception is TimeoutException,
                initialDelay: TimeSpan.Zero));

        Assert.AreEqual(1, attempts);
    }
}
