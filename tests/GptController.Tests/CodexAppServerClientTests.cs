using System.Diagnostics;
using System.Text.Json;
using GptController.Infrastructure;
using GptController.Services;

namespace GptController.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public void AccountSessionsUseActiveSessionsExactSelectedWorkspace()
    {
        var metadata = new AccountReadMetadata(
            "fallback@example.com",
            "business",
            "workspace-fallback");
        using var document = JsonDocument.Parse(
            """
            {
              "activeSessionId": "session-current",
              "sessions": [
                {
                  "sessionId": "session-other",
                  "email": "other@example.com",
                  "isActive": false,
                  "selectedWorkspaceAccountId": "workspace-other",
                  "workspaces": [
                    { "accountId": "workspace-other", "name": "其他组织", "kind": "workspace" }
                  ]
                },
                {
                  "sessionId": "session-current",
                  "email": "current@example.com",
                  "isActive": true,
                  "selectedWorkspaceAccountId": "workspace-current",
                  "workspaces": [
                    { "accountId": "workspace-decoy", "name": "错误组织", "kind": "workspace" },
                    { "accountId": "workspace-current", "name": "当前组织", "kind": "workspace" }
                  ]
                }
              ]
            }
            """);

        var enriched = CodexAppServerClient.EnrichWithAccountSessions(
            metadata,
            document.RootElement);

        Assert.Equal("current@example.com", enriched.Email);
        Assert.Equal("workspace-current", enriched.AccountId);
        Assert.Equal(AccountWorkspaceKind.Workspace, enriched.WorkspaceKind);
        Assert.Equal("当前组织", enriched.WorkspaceName);
    }

    [Fact]
    public void AccountSessionsExposePersonalWorkspaceKind()
    {
        var metadata = new AccountReadMetadata(null, "plus", null);
        using var document = JsonDocument.Parse(
            """
            {
              "sessions": [
                {
                  "sessionId": "session-personal",
                  "email": "personal@example.com",
                  "isActive": true,
                  "selectedWorkspaceAccountId": "personal-account",
                  "workspaces": [
                    { "accountId": "personal-account", "name": "Personal", "kind": "personal" }
                  ]
                }
              ]
            }
            """);

        var enriched = CodexAppServerClient.EnrichWithAccountSessions(
            metadata,
            document.RootElement);

        Assert.Equal("personal-account", enriched.AccountId);
        Assert.Equal(AccountWorkspaceKind.Personal, enriched.WorkspaceKind);
    }

    [Theory]
    [InlineData("-32601", "No such RPC", true)]
    [InlineData(null, "Method not found: account/sessions/list", true)]
    [InlineData("401", "Unauthorized", false)]
    public void AccountSessionsCapabilityDetectionOnlyClassifiesMissingMethods(
        string? errorCode,
        string message,
        bool expected)
    {
        var exception = new CodexAppServerException(
            "account/sessions/list",
            errorCode,
            message);

        Assert.Equal(
            expected,
            CodexAppServerClient.IsUnavailableAccountSessionsMethod(exception));
    }

    [Fact]
    public async Task UnresponsiveProcessIsKilledAfterGracePeriod()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GptController.Tests",
            Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(testRoot, testRoot);
        var logger = new RedactingLogger(paths);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start test process.");

        try
        {
            var stopwatch = Stopwatch.StartNew();

            await CodexAppServerClient.StopProcessAsync(process, logger);

            stopwatch.Stop();
            Assert.True(process.HasExited);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 400, 2500);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
