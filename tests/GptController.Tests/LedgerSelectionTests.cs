using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;
using GptController.ViewModels;

namespace GptController.Tests;

public sealed class LedgerSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ledger-selection-{Guid.NewGuid():N}");

    [Fact]
    public void SelectingAnAccountChangesInspectorWithoutSwitchingActiveConnection()
    {
        using var model = CreateModel();
        model.RefreshAllCommand.Execute(null);
        var active = model.CurrentConnection;
        var target = model.Accounts.Single(account => account.LedgerName == "工作账号");
        model.SelectedConnection = target;
        Assert.Same(target, model.SelectedConnection);
        Assert.Same(active, model.CurrentConnection);
        Assert.False(target.IsActive);
    }

    [Fact]
    public void RefreshRestoresSelectedIdentityAfterItemsAreRecreated()
    {
        using var model = CreateModel();
        model.RefreshAllCommand.Execute(null);
        var before = model.Accounts.Single(account => account.LedgerName == "工作账号");
        model.SelectedConnection = before;
        model.RefreshAllCommand.Execute(null);
        Assert.Equal(before.Id, model.SelectedConnection!.Id);
        Assert.NotSame(before, model.SelectedConnection);
        Assert.NotNull(model.CurrentConnection);
    }

    [Fact]
    public void RemovingSelectedConnectionFallsBackToTheActiveAccount()
    {
        using var model = CreateModel();
        model.RefreshAllCommand.Execute(null);
        Assert.True(model.SelectedConnection!.IsDeepSeek);
        model.Accounts.Remove(model.SelectedConnection);
        model.NotifyConnectionsChanged();
        Assert.Same(model.CurrentConnection, model.SelectedConnection);
    }

    [Fact]
    public void EmptyLedgerClearsInspectorAndFreshnessWithoutThrowing()
    {
        using var model = CreateModel();
        model.RefreshAllCommand.Execute(null);
        model.Accounts.Clear();
        model.NotifyConnectionsChanged();
        Assert.False(model.HasConnections);
        Assert.False(model.HasSelectedConnection);
        Assert.False(model.HasCurrentConnection);
        Assert.Equal("等待同步", model.LatestSyncText);
    }

    [Fact]
    public void ApiDetailsCanBindCreditCaptionWithoutReadingAnOAuthProfile()
    {
        Assert.Equal("Credits · 未提供", new AccountCardViewModel(new DeepSeekConnection()).CreditBalanceText);
        var qwen = new AccountCardViewModel(new QwenConnection { Model = "qwen-test" });
        Assert.Equal("未提供", qwen.LedgerPrimaryValue);
        Assert.Equal("未提供", qwen.LedgerSecondaryValue);
    }

    private MainWindowViewModel CreateModel()
    {
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        var vault = new ProfileVault(paths);
        var switcher = new SwitchCoordinator(paths, vault, new InactiveProcess(), new OperationGate(), new RedactingLogger(paths));
        return new MainWindowViewModel(
            vault, null!, null!, null!, null!, null!, switcher, new InactiveProcess(), null!,
            null!, null!, null!, null!, null!, null!, "helper.exe", isUiPreview: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InactiveProcess : IChatGptProcessController
    {
        public bool IsChatGptRunning() => false;
        public Task<bool> StopChatGptAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> FindBlockingCodexProcessesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> LaunchChatGptAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
