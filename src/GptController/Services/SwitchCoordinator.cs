using System.Security.Cryptography;
using System.Text.Json;
using GptController.Infrastructure;
using GptController.Models;

namespace GptController.Services;

public sealed record SwitchJournal(
    Guid? PreviousProfileId,
    Guid TargetProfileId,
    bool PreviousAuthExisted,
    string? BackupName,
    DateTimeOffset StartedAt);

public sealed class SwitchCoordinator
{
    private const string MutexName = LegacyCompatibility.SwitchMutexName;
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

    public bool HasPendingTransaction => File.Exists(_paths.TransactionFile);

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
        IProgress<SwitchStage>? progress = null,
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
            return await SwitchCoreAsync(targetProfileId, progress, cancellationToken);
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
            if (!await _processController.StopChatGptAsync(cancellationToken))
            {
                await _logger.WarningAsync(
                    "switch.recovery",
                    "ChatGPT could not be stopped; recovery was deferred.");
                return false;
            }

            await RestorePreviousAuthAsync(journal, cancellationToken);
            if (journal.PreviousProfileId.HasValue)
            {
                await _vault.SetActiveProfileAsync(journal.PreviousProfileId.Value, cancellationToken);
            }

            if (journal.PreviousAuthExisted)
            {
                if (!await _processController.LaunchChatGptAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "ChatGPT did not restart after switch recovery.");
                }
            }

            DeleteTransactionFileReliably();
            await _logger.WarningAsync("switch.recovery", "Recovered an incomplete switch transaction.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.recovery", exception);
            return false;
        }
    }

    internal async Task<string?> CaptureActiveCredentialBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await _vault.GetActiveProfileAsync(cancellationToken);
        if (active is null || !File.Exists(_paths.LiveAuthFile))
        {
            return null;
        }

        var liveCredential = await File.ReadAllBytesAsync(
            _paths.LiveAuthFile,
            cancellationToken);
        try
        {
            await CaptureOutgoingCredentialCoreAsync(
                active,
                liveCredential,
                cancellationToken);
            return await _vault.CreateBackupAsync(
                liveCredential,
                active.Id,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(liveCredential);
        }
    }

    internal async Task<SwitchResult> SwitchCoreAsync(
        Guid targetProfileId,
        IProgress<SwitchStage>? progress,
        CancellationToken cancellationToken)
    {
        if (HasPendingTransaction)
        {
            return SwitchResult.Failure(
                SwitchStatus.Failed,
                "检测到尚未完成的账号恢复。请完全关闭 ChatGPT，然后重启本应用完成恢复。");
        }

        progress?.Report(SwitchStage.ValidatingCredential);
        var target = await _vault.GetProfileAsync(targetProfileId, cancellationToken);
        if (target is null)
        {
            return SwitchResult.Failure(SwitchStatus.AuthenticationInvalid, "目标账号不存在。");
        }

        if (target.IsActive)
        {
            return SwitchResult.Success("该账号已经是当前账号。");
        }

        byte[]? targetCredential = null;
        byte[]? previousLive = null;
        try
        {
            try
            {
                targetCredential = await _vault.ReadCredentialAsync(
                    target.Id,
                    cancellationToken);
                ValidateCredentialIdentity(targetCredential, target.AccountId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync("switch.target", exception);
                return SwitchResult.Failure(
                    SwitchStatus.AuthenticationInvalid,
                    "目标账号认证无效，请重新添加该账号。");
            }

            var wasChatGptRunning = _processController.IsChatGptRunning();
            progress?.Report(SwitchStage.StoppingChatGpt);
            if (!await _processController.StopChatGptAsync(cancellationToken))
            {
                await RestartIfStoppedAsync(wasChatGptRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ProcessBlocked,
                    "无法关闭 ChatGPT，请关闭客户端后重试。");
            }

            progress?.Report(SwitchStage.CheckingBlockers);
            IReadOnlyList<string> blockers;
            try
            {
                blockers = await _processController.FindBlockingCodexProcessesAsync(
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                await RestartIfStoppedAsync(wasChatGptRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ProcessBlocked,
                    "检查共享认证进程超时。为保护当前登录状态，已取消切换。");
            }

            if (blockers.Count > 0)
            {
                await RestartIfStoppedAsync(wasChatGptRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ProcessBlocked,
                    "仍有共享认证的 Codex 服务正在运行：" + string.Join("、", blockers) + "。请关闭后重试。");
            }

            var previous = await _vault.GetActiveProfileAsync(cancellationToken);
            var previousAuthExisted = File.Exists(_paths.LiveAuthFile);
            previousLive = previousAuthExisted
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
                progress?.Report(SwitchStage.WritingCredential);
                Directory.CreateDirectory(_paths.CodexHome);
                await AtomicFile.WriteAllBytesAsync(
                    _paths.LiveAuthFile,
                    targetCredential,
                    cancellationToken);
                var written = await File.ReadAllBytesAsync(
                    _paths.LiveAuthFile,
                    cancellationToken);
                try
                {
                    ValidateCredentialIdentity(written, target.AccountId);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(written);
                }

                progress?.Report(SwitchStage.LaunchingChatGpt);
                if (!await _processController.LaunchChatGptAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "ChatGPT did not start within the expected time.");
                }

                await _vault.SetActiveProfileAsync(target.Id, cancellationToken);
                DeleteTransactionFileReliably();
                await _logger.InfoAsync("switch.complete", $"Activated profile {target.Id:N}.");
                progress?.Report(SwitchStage.Completed);
                return SwitchResult.Success();
            }
            catch (OperationCanceledException)
            {
                // The journal is intentionally retained if cancellation happened after
                // the live credential changed, so startup recovery can restore it.
                throw;
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync("switch.failed", exception);
                try
                {
                    if (!await _processController.StopChatGptAsync(CancellationToken.None))
                    {
                        throw new InvalidOperationException(
                            "ChatGPT could not be stopped for switch rollback.");
                    }

                    await RestorePreviousAuthAsync(journal, CancellationToken.None);
                    if (previous is not null)
                    {
                        await _vault.SetActiveProfileAsync(previous.Id, CancellationToken.None);
                    }

                    if (previousAuthExisted)
                    {
                        if (!await _processController.LaunchChatGptAsync(CancellationToken.None))
                        {
                            throw new InvalidOperationException(
                                "ChatGPT did not restart after switch rollback.");
                        }
                    }

                    DeleteTransactionFileReliably();
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
        finally
        {
            if (previousLive is not null)
            {
                CryptographicOperations.ZeroMemory(previousLive);
            }

            if (targetCredential is not null)
            {
                CryptographicOperations.ZeroMemory(targetCredential);
            }
        }
    }

    private void DeleteTransactionFileReliably()
    {
        File.Delete(_paths.TransactionFile);
        if (File.Exists(_paths.TransactionFile))
        {
            throw new IOException("The switch journal could not be deleted.");
        }
    }

    private async Task CaptureOutgoingCredentialAsync(
        AccountProfile previous,
        byte[] liveCredential,
        CancellationToken cancellationToken)
    {
        try
        {
            await CaptureOutgoingCredentialCoreAsync(
                previous,
                liveCredential,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.WarningAsync(
                "switch.capture",
                $"Outgoing credential was not captured: {exception.Message}");
        }
    }

    private async Task CaptureOutgoingCredentialCoreAsync(
        AccountProfile previous,
        byte[] liveCredential,
        CancellationToken cancellationToken)
    {
        ValidateCredentialIdentity(liveCredential, previous.AccountId);
        var stored = await _vault.ReadCredentialAsync(previous.Id, cancellationToken);
        try
        {
            var storedInfo = AuthDocument.Inspect(stored);
            var liveInfo = AuthDocument.Inspect(liveCredential);

            if (storedInfo.HasRefreshToken && !liveInfo.HasRefreshToken)
            {
                await _logger.WarningAsync(
                    "switch.capture",
                    $"Rejected incomplete live credential for profile {previous.Id:N}.");
                throw new InvalidDataException(
                    "The live ChatGPT credential is missing its refresh token.");
            }

            await _vault.WriteCredentialAsync(
                previous.Id,
                liveCredential,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
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
        try
        {
            await AtomicFile.WriteAllBytesAsync(
                _paths.LiveAuthFile,
                previousCredential,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousCredential);
        }
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
