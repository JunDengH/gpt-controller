using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class OperationGateTests
{
    [Fact]
    public async Task ConcurrentOperationsAreSerialized()
    {
        var gate = new OperationGate();
        var active = 0;
        var maximumActive = 0;

        async Task EnterAsync()
        {
            using var lease = await gate.EnterAsync();
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            await Task.Delay(30);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => EnterAsync()));

        Assert.Equal(1, maximumActive);
    }
}
