using System.Text;
using System.Text.Json;
using GptAccountManager.Credentials;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class ConnectionSwitchCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"provider-switch-{Guid.NewGuid():N}");

    [Fact]
    public async Task SwitchToDeepSeekAppliesSecretFreeConfigAndActivatesConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.True(result.IsSuccess);
        Assert.True((await fixture.ConnectionStore.GetAsync())!.IsActive);
        var config = await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile);
        Assert.Contains("gpt_controller_deepseek", config, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ApiKey, config, StringComparison.Ordinal);
        Assert.True(fixture.Process.IsRunning);
    }

    [Fact]
    public async Task LaunchFailureRestoresOriginalConfigAndConnectionState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);
        fixture.Process.QueueLaunchResults(false, true);

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.RolledBack, result.Status);
        Assert.False((await fixture.ConnectionStore.GetAsync())!.IsActive);
        Assert.True(fixture.Process.IsRunning);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task LaunchAndRollbackRestartFailureReportsFailed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);
        fixture.Process.QueueLaunchResults(false, false);

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.Failed, result.Status);
        Assert.False((await fixture.ConnectionStore.GetAsync())!.IsActive);
        Assert.False(fixture.Process.IsRunning);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task ChatGptRoundTripPreservesLatestLiveRefreshToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(
            launchSucceeds: true,
            withActiveChatGpt: true);

        var deepSeekResult = await fixture.Coordinator.SwitchToDeepSeekAsync();
        var chatGptResult = await fixture.Coordinator.SwitchToChatGptAsync(
            fixture.ChatGptProfile!.Id,
            forceConfigRestore: false);

        Assert.True(deepSeekResult.IsSuccess);
        Assert.True(chatGptResult.IsSuccess);
        Assert.False((await fixture.ConnectionStore.GetAsync())!.IsActive);
        Assert.True((await fixture.Vault.GetProfileAsync(
            fixture.ChatGptProfile.Id))!.IsActive);
        var liveAuth = await File.ReadAllTextAsync(fixture.Paths.LiveAuthFile);
        var storedAuth = Encoding.UTF8.GetString(
            await fixture.Vault.ReadCredentialAsync(fixture.ChatGptProfile.Id));
        Assert.Contains("refresh-new", liveAuth, StringComparison.Ordinal);
        Assert.Contains("refresh-new", storedAuth, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-old", liveAuth, StringComparison.Ordinal);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task MissingCredentialHelperPreventsConfigMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);
        File.Delete(fixture.HelperPath);

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.AuthenticationInvalid, result.Status);
        Assert.True(fixture.Process.IsRunning);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task PendingChatGptRecoveryPreventsProviderMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);
        await File.WriteAllTextAsync(
            fixture.Paths.TransactionFile,
            "pending recovery must not be overwritten");

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.Failed, result.Status);
        Assert.Equal(
            "pending recovery must not be overwritten",
            await File.ReadAllTextAsync(fixture.Paths.TransactionFile));
        Assert.False((await fixture.ConnectionStore.GetAsync())!.IsActive);
        Assert.True(fixture.Process.IsRunning);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task CorruptedCredentialPreventsConfigMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);
        var metadata = await fixture.CredentialStore.GetMetadataAsync();
        var credentialPath = Path.Combine(
            fixture.Paths.Root,
            "credentials",
            "deepseek",
            metadata!.CredentialFile);
        await File.WriteAllBytesAsync(credentialPath, [1, 2, 3, 4]);

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.AuthenticationInvalid, result.Status);
        Assert.True(fixture.Process.IsRunning);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task MismatchedLiveChatGptCredentialPreventsProviderSwitch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(
            launchSucceeds: true,
            withActiveChatGpt: true);
        await File.WriteAllBytesAsync(
            fixture.Paths.LiveAuthFile,
            CreateChatGptCredential("refresh-other", "other-account"));

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.Failed, result.Status);
        Assert.True(fixture.Process.IsRunning);
        Assert.True((await fixture.Vault.GetProfileAsync(
            fixture.ChatGptProfile!.Id))!.IsActive);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task IncompleteLiveChatGptCredentialPreventsProviderSwitch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(
            launchSucceeds: true,
            withActiveChatGpt: true);
        await File.WriteAllBytesAsync(
            fixture.Paths.LiveAuthFile,
            CreateChatGptCredential(refreshToken: null));

        var result = await fixture.Coordinator.SwitchToDeepSeekAsync();

        Assert.Equal(SwitchStatus.Failed, result.Status);
        Assert.True(fixture.Process.IsRunning);
        Assert.True((await fixture.Vault.GetProfileAsync(
            fixture.ChatGptProfile!.Id))!.IsActive);
        var storedAuth = Encoding.UTF8.GetString(
            await fixture.Vault.ReadCredentialAsync(fixture.ChatGptProfile.Id));
        Assert.Contains("refresh-old", storedAuth, StringComparison.Ordinal);
        Assert.Equal(
            "model = \"gpt-test\"" + Environment.NewLine,
            await File.ReadAllTextAsync(fixture.Paths.CodexConfigFile));
    }

    [Fact]
    public async Task FailedDeepSeekRollbackReportsFailureWhenChatGptDoesNotRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(
            launchSucceeds: true,
            withActiveChatGpt: true);
        Assert.True((await fixture.Coordinator.SwitchToDeepSeekAsync()).IsSuccess);
        fixture.Process.QueueLaunchResults(false, true, false);

        var result = await fixture.Coordinator.SwitchToChatGptAsync(
            fixture.ChatGptProfile!.Id,
            forceConfigRestore: false);

        Assert.Equal(SwitchStatus.Failed, result.Status);
        Assert.False(fixture.Process.IsRunning);
        Assert.True((await fixture.ConnectionStore.GetAsync())!.IsActive);
    }

    [Fact]
    public async Task StartupRecoveryCompletesInterruptedProviderApply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync(launchSucceeds: true);
        await fixture.ConfigService.ApplyAsync();
        var state = await File.ReadAllTextAsync(
            fixture.Paths.DeepSeekConfigStateFile);
        state = state.Replace(
            "\"phase\": \"applied\"",
            "\"phase\": \"applying\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            fixture.Paths.DeepSeekConfigStateFile,
            state);
        Assert.Contains("\"phase\": \"applying\"", state, StringComparison.Ordinal);

        var recovered = await fixture.Coordinator.RecoverProviderStateAsync();

        Assert.True(recovered);
        Assert.True((await fixture.ConnectionStore.GetAsync())!.IsActive);
        Assert.Contains(
            "\"phase\": \"applied\"",
            await File.ReadAllTextAsync(fixture.Paths.DeepSeekConfigStateFile),
            StringComparison.Ordinal);
    }

    private async Task<Fixture> CreateFixtureAsync(
        bool launchSucceeds,
        bool withActiveChatGpt = false)
    {
        var paths = new AppPaths(Path.Combine(_root, "local"), Path.Combine(_root, "profile"));
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.CodexHome);
        await File.WriteAllTextAsync(
            paths.CodexConfigFile,
            "model = \"gpt-test\"" + Environment.NewLine);
        var credentialStore = new DeepSeekCredentialStore(paths.Root);
        var connectionStore = new DeepSeekConnectionStore(paths, credentialStore);
        const string apiKey = "sk-provider-switch-secret-1234567890";
        await connectionStore.SaveAsync(
            new DeepSeekConnection { Nickname = "DeepSeek V4" },
            apiKey);
        var helper = Path.Combine(_root, "helper.exe");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(helper, []);
        var config = new DeepSeekCodexConfigService(
            new DeepSeekCodexConfigOptions(
                paths.CodexConfigFile,
                paths.DeepSeekModelCatalogFile,
                paths.DeepSeekConfigStateFile,
                helper));
        var process = new FakeProcessController(launchSucceeds);
        var gate = new OperationGate();
        var vault = new ProfileVault(paths);
        AccountProfile? chatGptProfile = null;
        if (withActiveChatGpt)
        {
            chatGptProfile = await vault.UpsertProfileAsync(
                new AccountProfile
                {
                    Nickname = "ChatGPT",
                    Email = "chatgpt@example.com",
                    AccountId = "chatgpt-account",
                    IsActive = true,
                    Ownership = AccountOwnership.Personal
                },
                CreateChatGptCredential("refresh-old"));
            await File.WriteAllBytesAsync(
                paths.LiveAuthFile,
                CreateChatGptCredential("refresh-new"));
        }

        var logger = new RedactingLogger(paths);
        var accountSwitch = new SwitchCoordinator(paths, vault, process, gate, logger);
        var coordinator = new ConnectionSwitchCoordinator(
            vault,
            connectionStore,
            credentialStore,
            config,
            accountSwitch,
            process,
            gate,
            logger,
            $@"Local\GptAccountManager.Tests.{Guid.NewGuid():N}");
        return new Fixture(
            paths,
            connectionStore,
            credentialStore,
            vault,
            config,
            process,
            coordinator,
            apiKey,
            helper,
            chatGptProfile);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private sealed record Fixture(
        AppPaths Paths,
        DeepSeekConnectionStore ConnectionStore,
        DeepSeekCredentialStore CredentialStore,
        ProfileVault Vault,
        DeepSeekCodexConfigService ConfigService,
        FakeProcessController Process,
        ConnectionSwitchCoordinator Coordinator,
        string ApiKey,
        string HelperPath,
        AccountProfile? ChatGptProfile);

    private sealed class FakeProcessController(bool launchSucceeds)
        : IChatGptProcessController
    {
        private readonly Queue<bool> _launchResults = new();

        public bool IsRunning { get; private set; } = true;

        public void QueueLaunchResults(params bool[] results)
        {
            foreach (var result in results)
            {
                _launchResults.Enqueue(result);
            }
        }

        public bool IsChatGptRunning() => IsRunning;

        public Task<bool> StopChatGptAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> LaunchChatGptAsync(CancellationToken cancellationToken = default)
        {
            var succeeds = _launchResults.Count > 0
                ? _launchResults.Dequeue()
                : launchSucceeds;
            IsRunning = succeeds;
            return Task.FromResult(succeeds);
        }
    }

    private static byte[] CreateChatGptCredential(
        string? refreshToken,
        string accountId = "chatgpt-account")
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] =
                    new Dictionary<string, string>
                    {
                        ["chatgpt_account_id"] = accountId
                    }
            });
        var jwt = $"{Base64Url(Encoding.UTF8.GetBytes("{}"))}.{Base64Url(payload)}.signature";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new
            {
                id_token = jwt,
                access_token = jwt,
                refresh_token = refreshToken,
                account_id = accountId
            }
        });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
