using GptAccountManager.Infrastructure;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class CodexAppServerIntegrationTests
{
    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task InstalledCodex_AppServerCompletesHandshakeWithIsolatedHome()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GAM_RUN_CODEX_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set GAM_RUN_CODEX_INTEGRATION=1 to run against the locally installed ChatGPT app.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "gam-codex-integration",
            Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, root);
        paths.EnsureCreated();

        try
        {
            var installation = await new CodexLocator(paths).LocateAsync();
            Assert.IsTrue(File.Exists(installation.CodexExecutable));
            Assert.IsTrue(
                installation.CodexExecutable.StartsWith(
                    paths.Runtime,
                    StringComparison.OrdinalIgnoreCase),
                "The restricted MSIX executable should be copied into the user runtime directory.");

            var isolatedHome = Path.Combine(paths.Temp, "probe-home");
            Directory.CreateDirectory(isolatedHome);
            await using var client = await CodexAppServerClient.StartAsync(
                installation.CodexExecutable,
                isolatedHome,
                new RedactingLogger(paths));
            var result = await client.RequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(15));

            Assert.IsTrue(result.TryGetProperty("account", out var account));
            Assert.AreEqual(JsonValueKind.Null, account.ValueKind);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort in local integration cleanup.
            }
        }
    }
}
