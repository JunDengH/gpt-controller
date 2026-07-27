using System.Text;
using System.Text.Json;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class QuotaServiceTests
{
    [Fact]
    public async Task ActiveProbeKeepsRefreshTokenInIsolatedCodexHome()
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: true,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10));

        var updated = await harness.Service.RefreshAsync(harness.Profile.Id);

        Assert.Equal(1, harness.Factory.StartCount);
        Assert.NotNull(harness.Factory.LastCredential);
        Assert.True(
            AuthDocument.Inspect(harness.Factory.LastCredential).HasRefreshToken);
        Assert.Equal(QuotaStatus.Fresh, updated.Quota?.Status);
    }

    [Fact]
    public async Task ActiveProbeIsDeferredNearAccessTokenExpiration()
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: true,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(4));

        var updated = await harness.Service.RefreshAsync(harness.Profile.Id);

        Assert.Equal(0, harness.Factory.StartCount);
        Assert.Equal(QuotaStatus.Stale, updated.Quota?.Status);
        Assert.Equal("active_refresh_deferred", updated.Quota?.ErrorCode);
    }

    [Fact]
    public async Task LegacyAuthenticationErrorIsRetriedAutomaticallyAndRecovers()
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: false,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            quota: new QuotaSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Status = QuotaStatus.AuthenticationRequired,
                ErrorCode = "authentication_required"
            });

        var refreshed = await harness.Service.RefreshAllAsync(
            QuotaRefreshReason.Automatic);

        Assert.Single(refreshed);
        Assert.Equal(1, harness.Factory.StartCount);
        Assert.Equal(QuotaStatus.Fresh, refreshed[0].Quota?.Status);
    }

    [Fact]
    public async Task ConfirmedAuthenticationErrorIsSkippedAutomatically()
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: false,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10),
            quota: new QuotaSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Status = QuotaStatus.AuthenticationRequired,
                ErrorCode = "confirmed_unauthorized"
            });

        var refreshed = await harness.Service.RefreshAllAsync(
            QuotaRefreshReason.Automatic);

        Assert.Empty(refreshed);
        Assert.Equal(0, harness.Factory.StartCount);
    }

    [Fact]
    public async Task ConcurrentAccountRefreshesUseOnlyOneAppServerAtATime()
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: false,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10));
        var second = await harness.AddProfileAsync("second-account");
        harness.Factory.ReadDelay = TimeSpan.FromMilliseconds(100);

        await Task.WhenAll(
            harness.Service.RefreshAsync(harness.Profile.Id),
            harness.Service.RefreshAsync(second.Id));

        Assert.Equal(2, harness.Factory.StartCount);
        Assert.Equal(1, harness.Factory.MaximumActiveSessions);
    }

    [Fact]
    public async Task CancellationDrainsTheActiveRefreshSession()
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: false,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10));
        harness.Factory.BlockReads = true;
        using var cancellation = new CancellationTokenSource();
        var refreshTask = harness.Service.RefreshAsync(
            harness.Profile.Id,
            cancellationToken: cancellation.Token);
        await harness.Factory.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await refreshTask);
        Assert.Equal(0, harness.Factory.ActiveSessions);
    }

    [Theory]
    [MemberData(nameof(RemoteFailureCases))]
    public async Task RemoteFailuresUseStrictAuthenticationClassification(
        Exception exception,
        QuotaStatus expectedStatus,
        string expectedErrorCode)
    {
        await using var harness = await QuotaHarness.CreateAsync(
            isActive: false,
            accessTokenExpiration: DateTimeOffset.UtcNow.AddMinutes(10));
        harness.Factory.AccountException = exception;

        var updated = await harness.Service.RefreshAsync(harness.Profile.Id);

        Assert.Equal(expectedStatus, updated.Quota?.Status);
        Assert.Equal(expectedErrorCode, updated.Quota?.ErrorCode);
    }

    public static TheoryData<Exception, QuotaStatus, string> RemoteFailureCases =>
        new()
        {
            {
                new CodexAppServerException(
                    "account/read",
                    "401",
                    "Request rejected."),
                QuotaStatus.AuthenticationRequired,
                "confirmed_unauthorized"
            },
            {
                new InvalidDataException(
                    "The app-server did not return a signed-in account."),
                QuotaStatus.Stale,
                "quota_refresh_failed"
            },
            {
                new TimeoutException("app-server request timed out."),
                QuotaStatus.Stale,
                "quota_refresh_failed"
            },
            {
                new InvalidOperationException("Service unavailable (503)."),
                QuotaStatus.Stale,
                "quota_refresh_failed"
            }
        };

    private sealed class QuotaHarness : IAsyncDisposable
    {
        private QuotaHarness(
            string testRoot,
            AccountProfile profile,
            ProfileVault vault,
            QuotaService service,
            RecordingAppServerFactory factory)
        {
            TestRoot = testRoot;
            Profile = profile;
            Vault = vault;
            Service = service;
            Factory = factory;
        }

        public string TestRoot { get; }
        public AccountProfile Profile { get; }
        public ProfileVault Vault { get; }
        public QuotaService Service { get; }
        public RecordingAppServerFactory Factory { get; }

        public static async Task<QuotaHarness> CreateAsync(
            bool isActive,
            DateTimeOffset accessTokenExpiration,
            QuotaSnapshot? quota = null)
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "GptAccountManager.Tests",
                Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(testRoot, testRoot);
            Directory.CreateDirectory(paths.Profiles);
            Directory.CreateDirectory(paths.Backups);
            Directory.CreateDirectory(paths.Temp);
            Directory.CreateDirectory(paths.Logs);
            Directory.CreateDirectory(paths.Runtime);
            Directory.CreateDirectory(paths.CodexHome);

            var profile = new AccountProfile
            {
                Nickname = "Test",
                Email = "test@example.com",
                AccountId = "account",
                IsActive = isActive,
                MembershipPlan = MembershipPlan.Plus,
                Ownership = AccountOwnership.Personal,
                Quota = quota
            };
            var credential = CreateCredential(
                accessTokenExpiration,
                "refresh-token");
            var vault = new ProfileVault(paths);
            profile = await vault.UpsertProfileAsync(profile, credential);
            if (isActive)
            {
                await AtomicFile.WriteAllBytesAsync(
                    paths.LiveAuthFile,
                    credential);
            }

            var factory = new RecordingAppServerFactory();
            var service = new QuotaService(
                paths,
                vault,
                new StubRuntimeLocator(),
                factory,
                new StubProcessController(isActive),
                new AccountMetadataService(),
                new QuotaParser(),
                new OperationGate(),
                new RedactingLogger(paths));
            return new QuotaHarness(
                testRoot,
                profile,
                vault,
                service,
                factory);
        }

        public async Task<AccountProfile> AddProfileAsync(string accountId)
        {
            var profile = new AccountProfile
            {
                Nickname = accountId,
                Email = $"{accountId}@example.com",
                AccountId = accountId,
                IsActive = false,
                MembershipPlan = MembershipPlan.Plus,
                Ownership = AccountOwnership.Personal
            };
            return await Vault.UpsertProfileAsync(
                profile,
                CreateCredential(
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    "refresh-token",
                    accountId));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(TestRoot))
            {
                Directory.Delete(TestRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubRuntimeLocator : ICodexRuntimeLocator
    {
        public Task<CodexInstallation> LocateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexInstallation(
                "test-codex.exe",
                null,
                "test-aumid"));
    }

    private sealed class StubProcessController(bool isRunning)
        : IChatGptProcessController
    {
        public bool IsChatGptRunning() => isRunning;

        public Task<bool> StopChatGptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> LaunchChatGptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingAppServerFactory
        : ICodexAppServerClientFactory
    {
        private int _activeSessions;
        private int _maximumActiveSessions;

        public int StartCount { get; private set; }
        public int ActiveSessions => Volatile.Read(ref _activeSessions);
        public int MaximumActiveSessions => _maximumActiveSessions;
        public byte[]? LastCredential { get; private set; }
        public Exception? AccountException { get; set; }
        public TimeSpan ReadDelay { get; set; }
        public bool BlockReads { get; set; }
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ICodexAppServerClient> StartAsync(
            string codexExecutable,
            string codexHome,
            RedactingLogger logger,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastCredential = await File.ReadAllBytesAsync(
                Path.Combine(codexHome, "auth.json"),
                cancellationToken);
            var accountId = JwtClaimsReader.Read(
                AuthDocument.Inspect(LastCredential)).AccountId
                ?? throw new InvalidDataException("Test credential has no account id.");
            var active = Interlocked.Increment(ref _activeSessions);
            UpdateMaximum(active);
            return new StubAppServerClient(
                accountId,
                AccountException,
                ReadDelay,
                BlockReads,
                ReadStarted,
                () => Interlocked.Decrement(ref _activeSessions));
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveSessions);
                if (active <= current ||
                    Interlocked.CompareExchange(
                        ref _maximumActiveSessions,
                        active,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class StubAppServerClient(
        string accountId,
        Exception? accountException,
        TimeSpan readDelay,
        bool blockReads,
        TaskCompletionSource readStarted,
        Action dispose)
        : ICodexAppServerClient
    {
        private bool _disposed;

        public async Task<AccountReadMetadata> ReadAccountAsync(
            CancellationToken cancellationToken = default)
        {
            if (readDelay > TimeSpan.Zero)
            {
                await Task.Delay(readDelay, cancellationToken);
            }

            if (blockReads)
            {
                readStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }

            if (accountException is not null)
            {
                throw accountException;
            }

            return new AccountReadMetadata(
                $"{accountId}@example.com",
                "plus",
                accountId);
        }

        public Task<JsonElement> ReadRateLimitsAsync(
            CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(
                """
                {
                  "rateLimits": {
                    "planType": "plus",
                    "primary": {
                      "usedPercent": 25,
                      "windowDurationMins": 10080,
                      "resetsAt": 1785200000
                    }
                  }
                }
                """);
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<LoginStartResult> StartChatGptLoginAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WaitForLoginCompletedAsync(
            string loginId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private static byte[] CreateCredential(
        DateTimeOffset accessTokenExpiration,
        string refreshToken,
        string accountId = "account")
    {
        var idToken = CreateJwt(new Dictionary<string, object?>
        {
            ["email"] = "test@example.com",
            ["https://api.openai.com/auth"] =
                    new Dictionary<string, string>
                {
                    ["chatgpt_account_id"] = accountId,
                    ["chatgpt_plan_type"] = "plus"
                }
        });
        var accessToken = CreateJwt(new Dictionary<string, object?>
        {
            ["exp"] = accessTokenExpiration.ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] =
                    new Dictionary<string, string>
                {
                    ["chatgpt_account_id"] = accountId
                }
        });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new
            {
                id_token = idToken,
                access_token = accessToken,
                refresh_token = refreshToken,
                account_id = accountId
            }
        });
    }

    private static string CreateJwt(object payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{}"));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.signature";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
