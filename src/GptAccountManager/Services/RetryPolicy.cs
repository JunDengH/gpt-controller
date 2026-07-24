namespace GptAccountManager.Services;

public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, bool> shouldRetry,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(shouldRetry);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var delay = initialDelay ?? TimeSpan.FromMilliseconds(500);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                exception is not OperationCanceledException &&
                shouldRetry(exception))
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, 10_000));
            }
        }
    }
}
