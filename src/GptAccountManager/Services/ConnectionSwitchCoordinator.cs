using GptAccountManager.Credentials;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed class ConnectionSwitchCoordinator
{
    private const string DefaultMutexName = @"Local\GptAccountManager.Switch";

    private readonly ProfileVault _vault;
    private readonly DeepSeekConnectionStore _deepSeekStore;
    private readonly DeepSeekCredentialStore _credentialStore;
    private readonly DeepSeekCodexConfigService _configService;
    private readonly SwitchCoordinator _accountSwitchCoordinator;
    private readonly IChatGptProcessController _processController;
    private readonly OperationGate _operationGate;
    private readonly RedactingLogger _logger;
    private readonly string _mutexName;

    public ConnectionSwitchCoordinator(
        ProfileVault vault,
        DeepSeekConnectionStore deepSeekStore,
        DeepSeekCredentialStore credentialStore,
        DeepSeekCodexConfigService configService,
        SwitchCoordinator accountSwitchCoordinator,
        IChatGptProcessController processController,
        OperationGate operationGate,
        RedactingLogger logger,
        string? mutexName = null)
    {
        _vault = vault;
        _deepSeekStore = deepSeekStore;
        _credentialStore = credentialStore;
        _configService = configService;
        _accountSwitchCoordinator = accountSwitchCoordinator;
        _processController = processController;
        _operationGate = operationGate;
        _logger = logger;
        _mutexName = mutexName ?? DefaultMutexName;
    }

    public Task<SwitchResult> SwitchToDeepSeekAsync(
        IProgress<SwitchStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => SwitchToDeepSeekCoreAsync(progress, cancellationToken),
            cancellationToken);

    public Task<SwitchResult> SwitchToChatGptAsync(
        Guid targetProfileId,
        bool forceConfigRestore,
        IProgress<SwitchStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => SwitchToChatGptCoreAsync(
                targetProfileId,
                forceConfigRestore,
                progress,
                cancellationToken),
            cancellationToken);

    public async Task<bool> RecoverProviderStateAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await _deepSeekStore.GetAsync(cancellationToken);
        var recovery = await _configService.RecoverInterruptedChangeAsync(
            cancellationToken);
        if (recovery.Status == DeepSeekConfigChangeStatus.Conflict)
        {
            throw new InvalidDataException(
                "Codex 配置在 DeepSeek 切换中断后发生冲突，请先恢复受管字段。");
        }

        if (recovery.Status == DeepSeekConfigChangeStatus.NotApplied)
        {
            if (connection?.IsActive == true)
            {
                await _deepSeekStore.SetActiveAsync(false, cancellationToken);
                return true;
            }

            return false;
        }

        if (connection is null)
        {
            var restore = await _configService.RestoreAsync(cancellationToken);
            return restore.Status == DeepSeekConfigChangeStatus.Restored;
        }

        await _vault.ClearActiveProfileAsync(cancellationToken);
        await _deepSeekStore.SetActiveAsync(true, cancellationToken);
        return !connection.IsActive;
    }

    private async Task<SwitchResult> SwitchToDeepSeekCoreAsync(
        IProgress<SwitchStage>? progress,
        CancellationToken cancellationToken)
    {
        if (_accountSwitchCoordinator.HasPendingTransaction)
        {
            return PendingAccountRecoveryFailure();
        }

        progress?.Report(SwitchStage.ValidatingCredential);
        var connection = await _deepSeekStore.GetAsync(cancellationToken);
        var credential = await _credentialStore.GetMetadataAsync(cancellationToken);
        if (connection is null || credential is null)
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "DeepSeek API Key 尚未配置。");
        }

        if (!File.Exists(_configService.CredentialHelperPath))
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "DeepSeek 凭据助手缺失，请重新安装完整的应用包。");
        }

        try
        {
            _ = await _credentialStore.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.WarningAsync(
                "switch.provider.credential",
                exception.GetType().Name);
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "DeepSeek API Key 无法解密或已损坏，请重新编辑连接。");
        }

        if (connection.IsActive && _configService.IsApplied)
        {
            return SwitchResult.Success("DeepSeek 已经是当前连接。");
        }

        var previous = await _vault.GetActiveProfileAsync(cancellationToken);
        var wasRunning = _processController.IsChatGptRunning();
        progress?.Report(SwitchStage.StoppingChatGpt);
        if (!await _processController.StopChatGptAsync(cancellationToken))
        {
            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "无法关闭或恢复 ChatGPT，请手动启动客户端并检查当前连接。");
            }

            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "无法关闭 ChatGPT，请关闭客户端后重试。");
        }

        var blockers = await FindBlockersAsync(wasRunning, cancellationToken);
        if (blockers is not null)
        {
            return blockers;
        }

        try
        {
            progress?.Report(SwitchStage.WritingCredential);
            await _accountSwitchCoordinator.CaptureActiveCredentialBackupAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await RestartIfNeededAsync(wasRunning, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.capture", exception);
            await RestartIfNeededAsync(wasRunning, CancellationToken.None);
            return SwitchResult.Failure(
                SwitchStatus.Failed,
                "无法安全备份当前 ChatGPT 认证，已取消切换。");
        }

        try
        {
            progress?.Report(SwitchStage.ConfiguringProvider);
            var config = await _configService.ApplyAsync(cancellationToken);
            if (config.Status == DeepSeekConfigChangeStatus.Conflict)
            {
                if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
                {
                    return SwitchResult.Failure(
                        SwitchStatus.Failed,
                        "Codex 配置存在冲突，且 ChatGPT 未能重新启动。");
                }

                return SwitchResult.Failure(
                    SwitchStatus.ConfigurationConflict,
                    "Codex 配置中的 DeepSeek 受管字段已被修改。请先恢复配置后重试。");
            }

            await _vault.ClearActiveProfileAsync(cancellationToken);
            await _deepSeekStore.SetActiveAsync(true, cancellationToken);

            progress?.Report(SwitchStage.LaunchingChatGpt);
            if (!await _processController.LaunchChatGptAsync(cancellationToken))
            {
                throw new InvalidOperationException("ChatGPT did not start after applying DeepSeek.");
            }

            progress?.Report(SwitchStage.Completed);
            await _logger.InfoAsync("switch.provider", "Activated DeepSeek Responses provider.");
            return SwitchResult.Success("已切换到 DeepSeek V4 Flash。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider", exception);
            try
            {
                var restored = await _configService.RestoreAsync(CancellationToken.None);
                if (restored.Status is not (
                        DeepSeekConfigChangeStatus.Restored or
                        DeepSeekConfigChangeStatus.NotApplied))
                {
                    throw new InvalidOperationException(
                        "The original Codex configuration could not be restored.");
                }

                await _deepSeekStore.SetActiveAsync(false, CancellationToken.None);
                if (previous is not null)
                {
                    await _vault.SetActiveProfileAsync(previous.Id, CancellationToken.None);
                }

                if (!await RestartIfNeededAsync(wasRunning, CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "ChatGPT did not restart after restoring the original provider.");
                }

                return SwitchResult.Failure(
                    SwitchStatus.RolledBack,
                    "切换 DeepSeek 失败，已恢复原连接。");
            }
            catch (Exception rollbackException)
            {
                await _logger.ErrorAsync("switch.provider.rollback", rollbackException);
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "切换和自动恢复均失败，请暂时不要启动 ChatGPT。");
            }
        }
    }

    private async Task<SwitchResult> SwitchToChatGptCoreAsync(
        Guid targetProfileId,
        bool forceConfigRestore,
        IProgress<SwitchStage>? progress,
        CancellationToken cancellationToken)
    {
        if (_accountSwitchCoordinator.HasPendingTransaction)
        {
            return PendingAccountRecoveryFailure();
        }

        var target = await _vault.GetProfileAsync(targetProfileId, cancellationToken);
        if (target is null)
        {
            return SwitchResult.Failure(SwitchStatus.AuthenticationInvalid, "目标账号不存在。");
        }

        var deepSeek = await _deepSeekStore.GetAsync(cancellationToken);
        if (deepSeek?.IsActive != true && !_configService.IsApplied)
        {
            return await _accountSwitchCoordinator.SwitchCoreAsync(
                targetProfileId,
                progress,
                cancellationToken);
        }

        var wasRunning = _processController.IsChatGptRunning();
        progress?.Report(SwitchStage.StoppingChatGpt);
        if (!await _processController.StopChatGptAsync(cancellationToken))
        {
            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "无法关闭或恢复 ChatGPT，请手动启动客户端并检查当前连接。");
            }

            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "无法关闭 ChatGPT，请关闭客户端后重试。");
        }

        var blockers = await FindBlockersAsync(wasRunning, cancellationToken);
        if (blockers is not null)
        {
            return blockers;
        }

        progress?.Report(SwitchStage.ConfiguringProvider);
        DeepSeekConfigChangeResult restore;
        try
        {
            restore = forceConfigRestore
                ? await _configService.ForceRestoreFromBackupAsync(cancellationToken)
                : await _configService.RestoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.restore", exception);
            return await RollBackToDeepSeekAsync();
        }

        if (restore.Status == DeepSeekConfigChangeStatus.Conflict)
        {
            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "Codex 配置存在冲突，且 ChatGPT 未能重新启动。");
            }

            return SwitchResult.Failure(
                SwitchStatus.ConfigurationConflict,
                "DeepSeek 启用后 Codex 的受管配置发生了修改。可确认使用加密备份强制恢复，或取消后手动处理。");
        }

        SwitchResult result;
        try
        {
            await _deepSeekStore.SetActiveAsync(false, cancellationToken);
            result = await _accountSwitchCoordinator.SwitchCoreAsync(
                targetProfileId,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.chatgpt", exception);
            return await RollBackToDeepSeekAsync();
        }

        if (result.IsSuccess)
        {
            return result;
        }

        return await RollBackToDeepSeekAsync();
    }

    private async Task<SwitchResult> RollBackToDeepSeekAsync()
    {
        try
        {
            if (!await _processController.StopChatGptAsync(CancellationToken.None))
            {
                throw new InvalidOperationException(
                    "ChatGPT could not be stopped before provider rollback.");
            }

            var reapplied = await _configService.ApplyAsync(CancellationToken.None);
            if (reapplied.Status is DeepSeekConfigChangeStatus.Applied or
                DeepSeekConfigChangeStatus.AlreadyApplied)
            {
                await _vault.ClearActiveProfileAsync(CancellationToken.None);
                await _deepSeekStore.SetActiveAsync(true, CancellationToken.None);
                if (!await _processController.LaunchChatGptAsync(CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "ChatGPT did not start after restoring DeepSeek.");
                }

                return SwitchResult.Failure(
                    SwitchStatus.RolledBack,
                    "切换 ChatGPT 失败，已恢复 DeepSeek 连接。");
            }

            throw new InvalidOperationException(
                "DeepSeek configuration could not be restored after a failed switch.");
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.rollback", exception);
            return SwitchResult.Failure(
                SwitchStatus.Failed,
                "切换 ChatGPT 失败，且自动恢复 DeepSeek 也失败。请暂时不要启动 ChatGPT。");
        }
    }

    private static SwitchResult PendingAccountRecoveryFailure() =>
        SwitchResult.Failure(
            SwitchStatus.Failed,
            "检测到尚未完成的账号恢复。请完全关闭 ChatGPT，然后重启本应用完成恢复。");

    private async Task<SwitchResult?> FindBlockersAsync(
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        try
        {
            var blockers = await _processController.FindBlockingCodexProcessesAsync(
                cancellationToken);
            if (blockers.Count == 0)
            {
                return null;
            }

            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "检测到共享认证进程，且 ChatGPT 未能重新启动。");
            }

            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "仍有共享认证的 Codex 服务正在运行：" + string.Join("、", blockers) + "。请关闭后重试。");
        }
        catch (TimeoutException)
        {
            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "共享认证进程检查超时，且 ChatGPT 未能重新启动。");
            }

            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "检查共享认证进程超时。为保护当前连接，已取消切换。");
        }
    }

    private async Task<SwitchResult> RunExclusiveAsync(
        Func<Task<SwitchResult>> operation,
        CancellationToken cancellationToken)
    {
        using var systemSemaphore = new Semaphore(1, 1, _mutexName);
        var ownsSemaphore = systemSemaphore.WaitOne(0);
        if (!ownsSemaphore)
        {
            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "另一个连接切换正在进行。");
        }

        try
        {
            using var gate = await _operationGate.EnterAsync(cancellationToken);
            return await operation();
        }
        finally
        {
            systemSemaphore.Release();
        }
    }

    private async Task<bool> RestartIfNeededAsync(
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        if (wasRunning && !_processController.IsChatGptRunning())
        {
            return await _processController.LaunchChatGptAsync(cancellationToken);
        }

        return true;
    }
}
