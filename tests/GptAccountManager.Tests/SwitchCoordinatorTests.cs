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
        Assert.False(File.Exists(harness.Paths.TransactionFile));
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

    [Fact]
    public async Task RecoveryStopFailurePreservesJournalAndLiveCredential()
    {
        var processController = new ScriptedProcessController();
        processController.QueueStopResults(false);
        await using var harness = await SwitchHarness.CreateAsync(
            processController,
            withPreviousProfile: true);
        var targetCredential = await harness.CreatePendingJournalAsync();
        var originalJournal = await File.ReadAllBytesAsync(harness.Paths.TransactionFile);

        var recovered = await harness.Coordinator.RecoverPendingTransactionAsync();
        var switchResult = await harness.Coordinator.SwitchAsync(harness.Profile.Id);

        Assert.False(recovered);
        Assert.Equal(SwitchStatus.Failed, switchResult.Status);
        Assert.True(File.Exists(harness.Paths.TransactionFile));
        Assert.Equal(
            originalJournal,
            await File.ReadAllBytesAsync(harness.Paths.TransactionFile));
        Assert.Equal(targetCredential, await File.ReadAllBytesAsync(harness.Paths.LiveAuthFile));
        Assert.True((await harness.Vault.GetProfileAsync(
            harness.PreviousProfile!.Id))!.IsActive);
        Assert.Equal(0, processController.LaunchCount);
        Assert.Equal(1, processController.StopCount);
    }

    [Fact]
    public async Task RecoveryLaunchFailurePreservesJournalAndReportsFailure()
    {
        var processController = new ScriptedProcessController();
        processController.QueueLaunchResults(false);
        await using var harness = await SwitchHarness.CreateAsync(
            processController,
            withPreviousProfile: true);
        await harness.CreatePendingJournalAsync();

        var recovered = await harness.Coordinator.RecoverPendingTransactionAsync();

        Assert.False(recovered);
        Assert.True(File.Exists(harness.Paths.TransactionFile));
        Assert.Equal(
            CreateCredential("previous-account"),
            await File.ReadAllBytesAsync(harness.Paths.LiveAuthFile));
        Assert.True((await harness.Vault.GetProfileAsync(
            harness.PreviousProfile!.Id))!.IsActive);
        Assert.False(processController.IsChatGptRunning());
    }

    [Fact]
    public async Task RollbackLaunchFailureReportsFailedAndPreservesJournal()
    {
        var processController = new ScriptedProcessController();
        processController.QueueLaunchResults(false, false);
        await using var harness = await SwitchHarness.CreateAsync(
            processController,
            withPreviousProfile: true);

        var result = await harness.Coordinator.SwitchAsync(harness.Profile.Id);

        Assert.Equal(SwitchStatus.Failed, result.Status);
        Assert.True(File.Exists(harness.Paths.TransactionFile));
        Assert.Equal(
            CreateCredential("previous-account"),
            await File.ReadAllBytesAsync(harness.Paths.LiveAuthFile));
        Assert.True((await harness.Vault.GetProfileAsync(
            harness.PreviousProfile!.Id))!.IsActive);
        Assert.False(processController.IsChatGptRunning());
    }

    [Fact]
    public async Task SuccessfulRollbackRestartsPreviousAccountAndDeletesJournal()
    {
        var processController = new ScriptedProcessController();
        processController.QueueLaunchResults(false, true);
        await using var harness = await SwitchHarness.CreateAsync(
            processController,
            withPreviousProfile: true);

        var result = await harness.Coordinator.SwitchAsync(harness.Profile.Id);

        Assert.Equal(SwitchStatus.RolledBack, result.Status);
        Assert.False(File.Exists(harness.Paths.TransactionFile));
        Assert.Equal(
            CreateCredential("previous-account"),
            await File.ReadAllBytesAsync(harness.Paths.LiveAuthFile));
        Assert.True((await harness.Vault.GetProfileAsync(
            harness.PreviousProfile!.Id))!.IsActive);
        Assert.True(processController.IsChatGptRunning());
    }

    private sealed class SwitchHarness : IAsyncDisposable
    {
        private SwitchHarness(
            string testRoot,
            AppPaths paths,
            ProfileVault vault,
            AccountProfile profile,
            AccountProfile? previousProfile,
            SwitchCoordinator coordinator)
        {
            TestRoot = testRoot;
            Paths = paths;
            Vault = vault;
            Profile = profile;
            PreviousProfile = previousProfile;
            Coordinator = coordinator;
        }

        public string TestRoot { get; }
        public AppPaths Paths { get; }
        public ProfileVault Vault { get; }
        public AccountProfile Profile { get; }
        public AccountProfile? PreviousProfile { get; }
        public SwitchCoordinator Coordinator { get; }

        public static async Task<SwitchHarness> CreateAsync(
            IChatGptProcessController processController,
            bool withPreviousProfile = false)
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
            AccountProfile? previousProfile = null;
            if (withPreviousProfile)
            {
                previousProfile = await vault.UpsertProfileAsync(
                    new AccountProfile
                    {
                        Nickname = "Previous",
                        Email = "previous@example.com",
                        AccountId = "previous-account",
                        IsActive = true,
                        Ownership = AccountOwnership.Personal
                    },
                    CreateCredential("previous-account"));
                await File.WriteAllBytesAsync(
                    paths.LiveAuthFile,
                    CreateCredential("previous-account"));
            }

            var profile = await vault.UpsertProfileAsync(
                new AccountProfile
                {
                    Nickname = "Target",
                    Email = "test@example.com",
                    AccountId = "account",
                    IsActive = false,
                    Ownership = AccountOwnership.Personal
                },
                CreateCredential("account"));
            var coordinator = new SwitchCoordinator(
                paths,
                vault,
                processController,
                new OperationGate(),
                new RedactingLogger(paths));
            return new SwitchHarness(
                testRoot,
                paths,
                vault,
                profile,
                previousProfile,
                coordinator);
        }

        public async Task<byte[]> CreatePendingJournalAsync()
        {
            var previous = PreviousProfile
                ?? throw new InvalidOperationException("A previous profile is required.");
            var previousCredential = CreateCredential(previous.AccountId);
            var backupName = await Vault.CreateBackupAsync(
                previousCredential,
                previous.Id);
            var journal = new SwitchJournal(
                previous.Id,
                Profile.Id,
                true,
                backupName,
                DateTimeOffset.UtcNow);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            await AtomicFile.WriteAllBytesAsync(
                Paths.TransactionFile,
                JsonSerializer.SerializeToUtf8Bytes(journal, options));
            var targetCredential = CreateCredential(Profile.AccountId);
            await File.WriteAllBytesAsync(Paths.LiveAuthFile, targetCredential);
            return targetCredential;
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

    private sealed class ScriptedProcessController : IChatGptProcessController
    {
        private readonly Queue<bool> _stopResults = new();
        private readonly Queue<bool> _launchResults = new();

        public bool IsRunning { get; private set; } = true;
        public int StopCount { get; private set; }
        public int LaunchCount { get; private set; }

        public void QueueStopResults(params bool[] results)
        {
            foreach (var result in results)
            {
                _stopResults.Enqueue(result);
            }
        }

        public void QueueLaunchResults(params bool[] results)
        {
            foreach (var result in results)
            {
                _launchResults.Enqueue(result);
            }
        }

        public bool IsChatGptRunning() => IsRunning;

        public Task<bool> StopChatGptAsync(
            CancellationToken cancellationToken = default)
        {
            StopCount++;
            var result = _stopResults.Count > 0 ? _stopResults.Dequeue() : true;
            if (result)
            {
                IsRunning = false;
            }

            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> LaunchChatGptAsync(
            CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            var result = _launchResults.Count > 0 ? _launchResults.Dequeue() : true;
            IsRunning = result;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingProgress<T>(Action<T> report)
        : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static byte[] CreateCredential(string accountId)
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
                refresh_token = "refresh-token",
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
