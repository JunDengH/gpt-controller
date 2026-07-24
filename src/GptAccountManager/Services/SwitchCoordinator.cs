using System.Text.Json;
using GptAccountManager.Infrastructure;
using GptAccountManager.Models;

namespace GptAccountManager.Services;

public sealed record SwitchJournal(
    Guid? PreviousProfileId,
    Guid TargetProfileId,
    bool PreviousAuthExisted,
    string? BackupName,
    DateTimeOffset StartedAt);

public sealed class SwitchCoordinator
{
    private const string MutexName = @"Local\GptAccountManager.Switch";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly ProfileVault _vault;
    private readonly IChatGptProcessController _processController;
    private readonly OperationGate _operationGate;
    private readonly RedactingLogger _logger;

    public SwitchCoordinator(
        AppPaths paths,
        ProfileVault vault,
        IChatGptProcessController processController,
        OperationGate operationGate,
        RedactingLogger logger)
    {
        _paths = paths;
        _vault = vault;
        _processController = processController;
        _operationGate = operationGate;
        _logger = logger;
    }

    public async Task<SwitchResult> SwitchAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        using var systemSemaphore = new Semaphore(1, 1, MutexName);
        var ownsSemaphore = false;
        try
        {
            ownsSemaphore = systemSemaphore.WaitOne(0);
            if (!ownsSemaphore)
            {
                return SwitchResult.Failure(
                    SwitchStatus.ProcessBlocked,
                    "另一个账号切换正在进行。");
            }

            using var operation = await _operationGate.EnterAsync(cancellationToken);
            return await SwitchCoreAsync(targetProfileId, cancellationToken);
        }
        finally
        {
            if (ownsSemaphore)
            {
                systemSemaphore.Release();
            }
        }
    }

    public async Task<bool> RecoverPendingTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.TransactionFile))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_paths.TransactionFile, cancellationToken);
            var journal = JsonSerializer.Deserialize<SwitchJournal>(json, JsonOptions)
                ?? throw new InvalidDataException("Invalid switch journal.");
            await _processController.StopChatGptAsync(cancellationToken);
            await RestorePreviousAuthAsync(journal, cancellationToken);
            if (journal.PreviousProfileId.HasValue)
            {
                await _vault.SetActiveProfileAsync(journal.PreviousProfileId.Value, cancellationToken);
            }

            AtomicFile.TryDelete(_paths.TransactionFile);
            if (journal.PreviousAuthExisted)
            {
                await _processController.LaunchChatGptAsync(cancellationToken);
            }

            await _logger.WarningAsync("switch.recovery", "Recovered an incomplete switch transaction.");
            return true;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.recovery", exception);
            return false;
        }
    }

    private async Task<SwitchResult> SwitchCoreAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken)
    {
        var target = await _vault.GetProfileAsync(targetProfileId, cancellationToken);
        if (target is null)
        {
            return SwitchResult.Failure(SwitchStatus.AuthenticationInvalid, "目标账号不存在。");
        }

        if (target.IsActive)
        {
            return SwitchResult.Success("该账号已经是当前账号。");
        }

        byte[] targetCredential;
        try
        {
            targetCredential = await _vault.ReadCredentialAsync(target.Id, cancellationToken);
            ValidateCredentialIdentity(targetCredential, target.AccountId);
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.target", exception);
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "目标账号认证无效，请重新添加该账号。");
        }

        var wasChatGptRunning = _processController.IsChatGptRunning();
        if (!await _processController.StopChatGptAsync(cancellationToken))
        {
            await RestartIfStoppedAsync(wasChatGptRunning, cancellationToken);
            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "无法关闭 ChatGPT，请关闭客户端后重试。");
        }

        var blockers = await _processController.FindBlockingCodexProcessesAsync(cancellationToken);
        if (blockers.Count > 0)
        {
            await RestartIfStoppedAsync(wasChatGptRunning, cancellationToken);
            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "仍有共享认证的 Codex 服务正在运行：" + string.Join("、", blockers) + "。请关闭后重试。");
        }

        var previous = await _vault.GetActiveProfileAsync(cancellationToken);
        var previousAuthExisted = File.Exists(_paths.LiveAuthFile);
        byte[]? previousLive = previousAuthExisted
            ? await File.ReadAllBytesAsync(_paths.LiveAuthFile, cancellationToken)
            : null;

        if (previous is not null && previousLive is not null)
        {
            await CaptureOutgoingCredentialAsync(previous, previousLive, cancellationToken);
        }

        var backupName = previousLive is null
            ? null
            : await _vault.CreateBackupAsync(
                previousLive,
                previous?.Id ?? Guid.Empty,
                cancellationToken);
        var journal = new SwitchJournal(
            previous?.Id,
            target.Id,
            previousAuthExisted,
            backupName,
            DateTimeOffset.UtcNow);
        await AtomicFile.WriteAllBytesAsync(
            _paths.TransactionFile,
            JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions),
            cancellationToken);

        try
        {
            Directory.CreateDirectory(_paths.CodexHome);
            await AtomicFile.WriteAllBytesAsync(
                _paths.LiveAuthFile,
                targetCredential,
                cancellationToken);
            var written = await File.ReadAllBytesAsync(_paths.LiveAuthFile, cancellationToken);
            ValidateCredentialIdentity(written, target.AccountId);

            if (!await _processController.LaunchChatGptAsync(cancellationToken))
            {
                throw new InvalidOperationException("ChatGPT did not start within the expected time.");
            }

            await _vault.SetActiveProfileAsync(target.Id, cancellationToken);
            AtomicFile.TryDelete(_paths.TransactionFile);
            await _logger.InfoAsync("switch.complete", $"Activated profile {target.Id:N}.");
            return SwitchResult.Success();
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.failed", exception);
            try
            {
                await _processController.StopChatGptAsync(cancellationToken);
                await RestorePreviousAuthAsync(journal, cancellationToken);
                if (previous is not null)
                {
                    await _vault.SetActiveProfileAsync(previous.Id, cancellationToken);
                }

                AtomicFile.TryDelete(_paths.TransactionFile);
                if (previousAuthExisted)
                {
                    await _processController.LaunchChatGptAsync(cancellationToken);
                }

                return SwitchResult.Failure(
                    SwitchStatus.RolledBack,
                    "切换失败，已恢复原账号。");
            }
            catch (Exception rollbackException)
            {
                await _logger.ErrorAsync("switch.rollback", rollbackException);
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "切换和自动恢复均失败。请不要启动 ChatGPT，并从应用的恢复状态重试。");
            }
        }
    }

    private async Task CaptureOutgoingCredentialAsync(
        AccountProfile previous,
        byte[] liveCredential,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateCredentialIdentity(liveCredential, previous.AccountId);
            var stored = await _vault.ReadCredentialAsync(previous.Id, cancellationToken);
            var storedInfo = AuthDocument.Inspect(stored);
            var liveInfo = AuthDocument.Inspect(liveCredential);

            if (storedInfo.HasRefreshToken && !liveInfo.HasRefreshToken)
            {
                await _logger.WarningAsync(
                    "switch.capture",
                    $"Skipped incomplete live credential for profile {previous.Id:N}.");
                return;
            }

            await _vault.WriteCredentialAsync(previous.Id, liveCredential, cancellationToken);
        }
        catch (Exception exception)
        {
            await _logger.WarningAsync(
                "switch.capture",
                $"Outgoing credential was not captured: {exception.Message}");
        }
    }

    private async Task RestorePreviousAuthAsync(
        SwitchJournal journal,
        CancellationToken cancellationToken)
    {
        if (!journal.PreviousAuthExisted)
        {
            AtomicFile.TryDelete(_paths.LiveAuthFile);
            return;
        }

        if (string.IsNullOrWhiteSpace(journal.BackupName))
        {
            throw new InvalidDataException("The recovery backup is missing.");
        }

        var previousCredential = await _vault.ReadBackupAsync(
            journal.BackupName,
            cancellationToken);
        await AtomicFile.WriteAllBytesAsync(
            _paths.LiveAuthFile,
            previousCredential,
            cancellationToken);
    }

    private async Task RestartIfStoppedAsync(
        bool wasChatGptRunning,
        CancellationToken cancellationToken)
    {
        if (wasChatGptRunning && !_processController.IsChatGptRunning())
        {
            await _processController.LaunchChatGptAsync(cancellationToken);
        }
    }

    private static void ValidateCredentialIdentity(
        ReadOnlySpan<byte> credential,
        string expectedAccountId)
    {
        var auth = AuthDocument.Inspect(credential);
        if (!auth.HasManagedTokens)
        {
            throw new InvalidDataException("Managed ChatGPT tokens are missing.");
        }

        var claims = JwtClaimsReader.Read(auth);
        if (string.IsNullOrWhiteSpace(claims.AccountId) ||
            !string.Equals(
                claims.AccountId,
                expectedAccountId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Credential identity does not match the account profile.");
        }
    }
}
