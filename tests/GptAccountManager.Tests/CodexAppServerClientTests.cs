using GptAccountManager.Infrastructure;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CodexAppServerClientTests
{
    private string _root = null!;
    private AppPaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "gam-protocol-tests",
            Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root, _root);
        _paths.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort in test cleanup.
        }
    }

    [TestMethod]
    public async Task AccountAndRateLimits_AreReadFromJsonRpcProcess()
    {
        await using var client = await StartClientAsync("normal");

        var account = await client.ReadAccountAsync();
        var limits = await client.ReadRateLimitsAsync();
        var quota = new QuotaParser().Parse(limits, DateTimeOffset.UtcNow);

        Assert.AreEqual("protocol@example.com", account.Email);
        Assert.AreEqual("plus", account.PlanType);
        Assert.AreEqual("account-protocol", account.AccountId);
        Assert.AreEqual("pro", quota.PlanType);
        Assert.AreEqual(72d, quota.Snapshot.RemainingPercent);
    }

    [TestMethod]
    public async Task LoginCompletionNotification_CanArriveBeforeStartResponse()
    {
        await using var client = await StartClientAsync(
            "login-notification-before-response");

        var login = await client.StartChatGptLoginAsync();
        await client.WaitForLoginCompletedAsync(
            login.LoginId,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual("login-protocol", login.LoginId);
        Assert.AreEqual("https://example.invalid/oauth", login.AuthUrl);
    }

    [TestMethod]
    public async Task ProcessExit_FaultsPendingRequest()
    {
        await using var client = await StartClientAsync("crash-on-account-read");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.ReadAccountAsync());

        StringAssert.Contains(exception.Message, "app-server exited");
    }

    private async Task<CodexAppServerClient> StartClientAsync(string scenario)
    {
        var home = Path.Combine(_paths.Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(
            Path.Combine(home, "fake-scenario.txt"),
            scenario);
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "fake-app-server",
            "FakeCodexAppServer.exe");
        Assert.IsTrue(File.Exists(executable), $"Fake app-server missing: {executable}");
        return await CodexAppServerClient.StartAsync(
            executable,
            home,
            new RedactingLogger(_paths));
    }
}
