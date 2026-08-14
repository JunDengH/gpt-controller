using System.Text;
using System.Text.Json;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class OAuthAccountServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DifferentFinalWorkspaceIsRejectedWithoutWritingAProfileOrCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePaths();
        var vault = new ProfileVault(paths);
        var oldCredential = CreateCredential("workspace-jwt", "old-refresh");
        var jwtProfile = await vault.UpsertProfileAsync(
            new AccountProfile
            {
                Nickname = "JWT workspace",
                Email = "jwt@example.com",
                AccountId = "workspace-jwt",
                MembershipPlan = MembershipPlan.Plus,
                Ownership = AccountOwnership.Personal
            },
            oldCredential);
        var newCredential = CreateCredential("workspace-jwt", "new-refresh");
        var service = CreateService(
            paths,
            vault,
            newCredential,
            new AccountReadMetadata(
                "selected@example.com",
                "business",
                "workspace-selected",
                AccountWorkspaceKind.Workspace,
                "Selected Workspace"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.AddAccountAsync());

        var profiles = await vault.LoadProfilesAsync();
        var unchangedJwt = Assert.Single(profiles);
        Assert.Contains("workspace 不一致", exception.Message, StringComparison.Ordinal);
        Assert.Equal(jwtProfile.Id, unchangedJwt.Id);
        Assert.Equal("workspace-jwt", unchangedJwt.AccountId);
        Assert.Equal("JWT workspace", unchangedJwt.Nickname);
        Assert.Equal(oldCredential, await vault.ReadCredentialAsync(unchangedJwt.Id));
        Assert.DoesNotContain(
            profiles,
            profile => profile.AccountId == "workspace-selected");
        Assert.Single(Directory.GetFiles(paths.Profiles, "*.bin"));
    }

    [Fact]
    public async Task MatchingCredentialReusesOnlyTheFinalWorkspaceProfileAndCache()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePaths();
        var vault = new ProfileVault(paths);
        var jwtCredential = CreateCredential("workspace-jwt", "jwt-old-refresh");
        var jwtProfile = await vault.UpsertProfileAsync(
            new AccountProfile
            {
                Nickname = "JWT workspace",
                Email = "jwt@example.com",
                AccountId = "workspace-jwt",
                MembershipPlan = MembershipPlan.Plus,
                Ownership = AccountOwnership.Personal
            },
            jwtCredential);
        var selectedOldCredential = CreateCredential(
            "workspace-selected",
            "selected-old-refresh");
        var selectedProfile = await vault.UpsertProfileAsync(
            new AccountProfile
            {
                Nickname = "Keep selected nickname",
                Email = "selected-cache@example.com",
                AccountId = "workspace-selected",
                MembershipPlan = MembershipPlan.Business,
                Ownership = AccountOwnership.Organization(
                    "workspace-selected",
                    "Cached Workspace")
            },
            selectedOldCredential);
        var newCredential = CreateCredential("workspace-selected", "new-refresh");
        var service = CreateService(
            paths,
            vault,
            newCredential,
            new AccountReadMetadata(
                null,
                null,
                "workspace-selected",
                AccountWorkspaceKind.Workspace));

        var added = await service.AddAccountAsync();

        Assert.Equal(selectedProfile.Id, added.Id);
        Assert.Equal("workspace-selected", added.AccountId);
        Assert.Equal("Keep selected nickname", added.Nickname);
        Assert.Equal("selected-cache@example.com", added.Email);
        Assert.Equal(MembershipPlan.Business, added.MembershipPlan);
        Assert.Equal("Cached Workspace", added.Ownership.DisplayName);
        Assert.Equal(newCredential, await vault.ReadCredentialAsync(added.Id));

        var unchangedJwt = await vault.GetProfileAsync(jwtProfile.Id);
        Assert.NotNull(unchangedJwt);
        Assert.Equal("workspace-jwt", unchangedJwt.AccountId);
        Assert.Equal("JWT workspace", unchangedJwt.Nickname);
        Assert.Equal(jwtCredential, await vault.ReadCredentialAsync(jwtProfile.Id));
        Assert.Equal(2, (await vault.LoadProfilesAsync()).Count);
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
            // Best-effort test cleanup.
        }
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        return paths;
    }

    private static OAuthAccountService CreateService(
        AppPaths paths,
        ProfileVault vault,
        byte[] credential,
        AccountReadMetadata accountRead) =>
        new(
            paths,
            vault,
            new StubRuntimeLocator(),
            new StubAppServerFactory(credential, accountRead),
            new AccountMetadataService(),
            new QuotaParser(),
            new OperationGate(),
            new RedactingLogger(paths),
            openBrowser: _ => { });

    private sealed class StubRuntimeLocator : ICodexRuntimeLocator
    {
        public Task<CodexInstallation> LocateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexInstallation(
                "test-codex.exe",
                null,
                "test-aumid"));
    }

    private sealed class StubAppServerFactory(
        byte[] credential,
        AccountReadMetadata accountRead)
        : ICodexAppServerClientFactory
    {
        public async Task<ICodexAppServerClient> StartAsync(
            string codexExecutable,
            string codexHome,
            RedactingLogger logger,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(codexHome, "auth.json"),
                credential,
                cancellationToken);
            return new StubAppServerClient(accountRead);
        }
    }

    private sealed class StubAppServerClient(AccountReadMetadata accountRead)
        : ICodexAppServerClient
    {
        public Task<AccountReadMetadata> ReadAccountAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(accountRead);

        public Task<JsonElement> ReadRateLimitsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<JsonElement>(
                new InvalidOperationException("Quota unavailable in this test."));

        public Task<LoginStartResult> StartChatGptLoginAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoginStartResult(
                "login-id",
                "https://example.invalid/login"));

        public Task WaitForLoginCompletedAsync(
            string loginId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] CreateCredential(string accountId, string refreshToken)
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
        var jwt =
            $"{Base64Url(Encoding.UTF8.GetBytes("{}"))}.{Base64Url(payload)}.signature";
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
