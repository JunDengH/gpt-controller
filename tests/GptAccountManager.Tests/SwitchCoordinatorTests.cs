using System.Text;
using System.Text.Json;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class SwitchCoordinatorTests
{
    [Fact]
    public async Task SuccessfulSwitchReportsEveryStageInOrder()
    {
        await using var harness = await SwitchHarness.CreateAsync(
            new SuccessfulProcessController());
        var stages = new List<SwitchStage>();

        var result = await harness.Coordinator.SwitchAsync(
            harness.Profile.Id,
            new RecordingProgress<SwitchStage>(stages.Add));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                SwitchStage.ValidatingCredential,
                SwitchStage.StoppingChatGpt,
                SwitchStage.CheckingBlockers,
                SwitchStage.WritingCredential,
                SwitchStage.LaunchingChatGpt,
                SwitchStage.Completed
            ],
            stages);
    }

    [Fact]
    public async Task CancellationStopsSwitchBeforeCredentialMutation()
    {
        var processController = new BlockingProcessController();
        await using var harness = await SwitchHarness.CreateAsync(processController);
        using var cancellation = new CancellationTokenSource();
        var switchTask = harness.Coordinator.SwitchAsync(
            harness.Profile.Id,
            cancellationToken: cancellation.Token);
        await processController.InspectionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await switchTask);
        Assert.False(File.Exists(harness.Paths.LiveAuthFile));
        Assert.False(File.Exists(harness.Paths.TransactionFile));
    }

    private sealed class SwitchHarness : IAsyncDisposable
    {
        private SwitchHarness(
            string testRoot,
            AppPaths paths,
            AccountProfile profile,
            SwitchCoordinator coordinator)
        {
            TestRoot = testRoot;
            Paths = paths;
            Profile = profile;
            Coordinator = coordinator;
        }

        public string TestRoot { get; }
        public AppPaths Paths { get; }
        public AccountProfile Profile { get; }
        public SwitchCoordinator Coordinator { get; }

        public static async Task<SwitchHarness> CreateAsync(
            IChatGptProcessController processController)
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
            Directory.CreateDirectory(paths.CodexHome);
            var vault = new ProfileVault(paths);
            var profile = await vault.UpsertProfileAsync(
                new AccountProfile
                {
                    Nickname = "Target",
                    Email = "test@example.com",
                    AccountId = "account",
                    IsActive = false,
                    Ownership = AccountOwnership.Personal
                },
                CreateCredential());
            var coordinator = new SwitchCoordinator(
                paths,
                vault,
                processController,
                new OperationGate(),
                new RedactingLogger(paths));
            return new SwitchHarness(
                testRoot,
                paths,
                profile,
                coordinator);
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

    private sealed class SuccessfulProcessController
        : IChatGptProcessController
    {
        public bool IsChatGptRunning() => false;

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

    private sealed class BlockingProcessController
        : IChatGptProcessController
    {
        public TaskCompletionSource InspectionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsChatGptRunning() => false;

        public Task<bool> StopChatGptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public async Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default)
        {
            InspectionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<bool> LaunchChatGptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingProgress<T>(Action<T> report)
        : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static byte[] CreateCredential()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] =
                    new Dictionary<string, string>
                    {
                        ["chatgpt_account_id"] = "account"
                    }
            });
        var jwt = $"{Base64Url(Encoding.UTF8.GetBytes("{}"))}.{Base64Url(payload)}.signature";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new
            {
                id_token = jwt,
                access_token = jwt,
                refresh_token = "refresh-token",
                account_id = "account"
            }
        });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
