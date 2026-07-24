using GptAccountManager.Infrastructure;
using GptAccountManager.Models;
using GptAccountManager.Services;

namespace GptAccountManager.Tests;

[TestClass]
public sealed class SwitchCoordinatorTests
{
    private string _root = null!;
    private AppPaths _paths = null!;
    private ProfileVault _vault = null!;
    private FakeProcessController _processes = null!;
    private SwitchCoordinator _coordinator = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "gam-switch-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root, _root);
        _paths.EnsureCreated();
        _vault = new ProfileVault(_paths);
        _processes = new FakeProcessController();
        _coordinator = new SwitchCoordinator(
            _paths,
            _vault,
            _processes,
            new OperationGate(),
            new RedactingLogger(_paths));
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
    public async Task Switch_ReplacesLiveAuthAndMarksTargetActive()
    {
        var (first, second) = await CreateTwoProfilesAsync();
        Directory.CreateDirectory(_paths.CodexHome);
        await File.WriteAllBytesAsync(_paths.LiveAuthFile, TestAuthFactory.Create("account-1"));

        var result = await _coordinator.SwitchAsync(second.Id);

        Assert.IsTrue(result.IsSuccess, result.Message);
        var live = AuthDocument.Inspect(await File.ReadAllBytesAsync(_paths.LiveAuthFile));
        Assert.AreEqual("account-2", JwtClaimsReader.Read(live).AccountId);
        Assert.AreEqual(second.Id, (await _vault.GetActiveProfileAsync())!.Id);
        Assert.IsFalse(File.Exists(_paths.TransactionFile));
        Assert.IsTrue(_processes.StopCalls > 0);
        Assert.IsTrue(_processes.LaunchCalls > 0);
        Assert.IsNotNull(first);
    }

    [TestMethod]
    public async Task Switch_LaunchFailureRollsBackLiveAuth()
    {
        var (_, second) = await CreateTwoProfilesAsync();
        Directory.CreateDirectory(_paths.CodexHome);
        var original = TestAuthFactory.Create("account-1");
        await File.WriteAllBytesAsync(_paths.LiveAuthFile, original);
        _processes.LaunchResults.Enqueue(false);
        _processes.LaunchResults.Enqueue(true);

        var result = await _coordinator.SwitchAsync(second.Id);

        Assert.AreEqual(SwitchStatus.RolledBack, result.Status);
        var live = AuthDocument.Inspect(await File.ReadAllBytesAsync(_paths.LiveAuthFile));
        Assert.AreEqual("account-1", JwtClaimsReader.Read(live).AccountId);
        Assert.AreEqual("account-1", (await _vault.GetActiveProfileAsync())!.AccountId);
        Assert.IsFalse(File.Exists(_paths.TransactionFile));
    }

    [TestMethod]
    public async Task Switch_BlockingOfficialAppServerRestartsPreviouslyRunningChatGpt()
    {
        var (_, second) = await CreateTwoProfilesAsync();
        Directory.CreateDirectory(_paths.CodexHome);
        var original = TestAuthFactory.Create("account-1");
        await File.WriteAllBytesAsync(_paths.LiveAuthFile, original);
        _processes.IsRunning = true;
        _processes.Blockers = ["Codex app-server（PID 123，Code.exe）"];

        var result = await _coordinator.SwitchAsync(second.Id);

        Assert.AreEqual(SwitchStatus.ProcessBlocked, result.Status);
        Assert.AreEqual(1, _processes.StopCalls);
        Assert.AreEqual(1, _processes.LaunchCalls);
        Assert.IsTrue(_processes.IsRunning);
        var live = AuthDocument.Inspect(await File.ReadAllBytesAsync(_paths.LiveAuthFile));
        Assert.AreEqual("account-1", JwtClaimsReader.Read(live).AccountId);
    }

    [TestMethod]
    public async Task Recovery_RestoresEncryptedSnapshotAndPreviousActiveProfile()
    {
        var (first, _) = await CreateTwoProfilesAsync();
        Directory.CreateDirectory(_paths.CodexHome);
        var original = TestAuthFactory.Create("account-1");
        var backupName = await _vault.CreateBackupAsync(original, first.Id);
        await File.WriteAllBytesAsync(
            _paths.LiveAuthFile,
            TestAuthFactory.Create("account-2"));
        var journal = new SwitchJournal(
            first.Id,
            Guid.NewGuid(),
            PreviousAuthExisted: true,
            backupName,
            DateTimeOffset.UtcNow);
        await AtomicFile.WriteAllBytesAsync(
            _paths.TransactionFile,
            JsonSerializer.SerializeToUtf8Bytes(
                journal,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
        _processes.IsRunning = true;

        var recovered = await _coordinator.RecoverPendingTransactionAsync();

        Assert.IsTrue(recovered);
        Assert.IsFalse(File.Exists(_paths.TransactionFile));
        Assert.AreEqual(first.Id, (await _vault.GetActiveProfileAsync())!.Id);
        Assert.AreEqual(1, _processes.StopCalls);
        Assert.AreEqual(1, _processes.LaunchCalls);
        Assert.IsTrue(_processes.IsRunning);
        var live = AuthDocument.Inspect(await File.ReadAllBytesAsync(_paths.LiveAuthFile));
        Assert.AreEqual("account-1", JwtClaimsReader.Read(live).AccountId);
    }

    private async Task<(AccountProfile First, AccountProfile Second)> CreateTwoProfilesAsync()
    {
        var first = await _vault.UpsertProfileAsync(
            new AccountProfile
            {
                Nickname = "First",
                Email = "first@example.com",
                AccountId = "account-1",
                IsActive = true
            },
            TestAuthFactory.Create("account-1"));
        var second = await _vault.UpsertProfileAsync(
            new AccountProfile
            {
                Nickname = "Second",
                Email = "second@example.com",
                AccountId = "account-2",
                IsActive = false
            },
            TestAuthFactory.Create("account-2"));
        return (first, second);
    }

    private sealed class FakeProcessController : IChatGptProcessController
    {
        public Queue<bool> LaunchResults { get; } = new();
        public int StopCalls { get; private set; }
        public int LaunchCalls { get; private set; }
        public bool IsRunning { get; set; }
        public IReadOnlyList<string> Blockers { get; set; } = [];

        public bool IsChatGptRunning() => IsRunning;

        public Task<bool> StopChatGptAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            IsRunning = false;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Blockers);

        public Task<bool> LaunchChatGptAsync(CancellationToken cancellationToken = default)
        {
            LaunchCalls++;
            var result = LaunchResults.Count > 0 ? LaunchResults.Dequeue() : true;
            IsRunning = result;
            return Task.FromResult(result);
        }
    }
}
