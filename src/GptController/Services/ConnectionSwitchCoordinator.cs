using GptController.Credentials;
using GptController.Infrastructure;
using GptController.Models;

namespace GptController.Services;

public sealed class ConnectionSwitchCoordinator
{
    private const string DefaultMutexName = LegacyCompatibility.SwitchMutexName;

    private readonly ProfileVault _vault;
    private readonly DeepSeekConnectionStore _deepSeekStore;
    private readonly DeepSeekCredentialStore _credentialStore;
    private readonly QwenConnectionStore? _qwenStore;
    private readonly QwenCredentialStore? _qwenCredentialStore;
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
        string? mutexName = null,
        QwenConnectionStore? qwenStore = null,
        QwenCredentialStore? qwenCredentialStore = null)
    {
        _vault = vault;
        _deepSeekStore = deepSeekStore;
        _credentialStore = credentialStore;
        _qwenStore = qwenStore;
        _qwenCredentialStore = qwenCredentialStore;
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
            () => SwitchToApiProviderCoreAsync(
                ConnectionProvider.DeepSeek,
                progress,
                cancellationToken),
            cancellationToken);

    public Task<SwitchResult> SwitchToQwenAsync(
        IProgress<SwitchStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => SwitchToApiProviderCoreAsync(
                ConnectionProvider.Qwen,
                progress,
                cancellationToken),
            cancellationToken);

    public Task<SwitchResult> SwitchToChatGptAsync(
        Guid targetProfileId,
        bool forceConfigRestore,
        IProgress<SwitchStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => SwitchToChatGptFromAnyProviderCoreAsync(
                targetProfileId,
                forceConfigRestore,
                progress,
                cancellationToken),
            cancellationToken);

    public Task<SwitchResult> ChangeDeepSeekModelAsync(
        string model,
        IProgress<SwitchStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => ChangeApiModelCoreAsync(
                ConnectionProvider.DeepSeek,
                model,
                null,
                progress,
                cancellationToken),
            cancellationToken);

    public Task<SwitchResult> ChangeApiModelAsync(
        ConnectionProvider provider,
        string model,
        DateTimeOffset? validatedAt = null,
        IProgress<SwitchStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => ChangeApiModelCoreAsync(
                provider,
                model,
                validatedAt,
                progress,
                cancellationToken),
            cancellationToken);

    public async Task<bool> RecoverProviderStateAsync(
        CancellationToken cancellationToken = default)
    {
        var deepSeek = await _deepSeekStore.GetAsync(cancellationToken);
        var qwen = _qwenStore is null
            ? null
            : await _qwenStore.GetAsync(cancellationToken);
        var recovery = await _configService.RecoverInterruptedChangeAsync(
            cancellationToken);
        if (recovery.Status == DeepSeekConfigChangeStatus.Conflict)
        {
            throw new InvalidDataException(
                "Codex 配置在 API 供应商切换中断后发生冲突，请先恢复受管字段。");
        }

        if (recovery.Status == DeepSeekConfigChangeStatus.NotApplied)
        {
            var changed = deepSeek?.IsActive == true || qwen?.IsActive == true;
            await SetApiActiveAsync(ConnectionProvider.DeepSeek, false, cancellationToken);
            await SetApiActiveAsync(ConnectionProvider.Qwen, false, cancellationToken);
            return changed;
        }

        var appliedProvider = string.Equals(
            recovery.ProviderId,
            DeepSeekCodexConfigService.QwenProviderId,
            StringComparison.Ordinal)
            ? ConnectionProvider.Qwen
            : ConnectionProvider.DeepSeek;
        var appliedExists = appliedProvider == ConnectionProvider.DeepSeek
            ? deepSeek is not null
            : qwen is not null;
        if (!appliedExists)
        {
            var restore = await _configService.RestoreAsync(cancellationToken);
            return restore.Status == DeepSeekConfigChangeStatus.Restored;
        }

        await _vault.ClearActiveProfileAsync(cancellationToken);
        await SetApiActiveAsync(ConnectionProvider.DeepSeek,
            appliedProvider == ConnectionProvider.DeepSeek,
            cancellationToken);
        await SetApiActiveAsync(ConnectionProvider.Qwen,
            appliedProvider == ConnectionProvider.Qwen,
            cancellationToken);
        return appliedProvider == ConnectionProvider.DeepSeek
            ? deepSeek?.IsActive != true || qwen?.IsActive == true
            : qwen?.IsActive != true || deepSeek?.IsActive == true;
    }

    private async Task<SwitchResult> SwitchToApiProviderCoreAsync(
        ConnectionProvider provider,
        IProgress<SwitchStage>? progress,
        CancellationToken cancellationToken)
    {
        if (_accountSwitchCoordinator.HasPendingTransaction)
        {
            return PendingAccountRecoveryFailure();
        }

        progress?.Report(SwitchStage.ValidatingCredential);
        ApiSwitchTarget? target;
        try
        {
            target = await LoadApiTargetAsync(
                provider,
                validateCredential: true,
                cancellationToken);
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
                provider == ConnectionProvider.Qwen
                    ? "千问 API Key 无法解密或已损坏，请重新配置连接。"
                    : "DeepSeek API Key 无法解密或已损坏，请重新配置连接。");
        }
        if (target is null)
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                provider == ConnectionProvider.Qwen
                    ? "千问 API Key 尚未配置。"
                    : "DeepSeek API Key 尚未配置。");
        }

        if (!File.Exists(_configService.CredentialHelperPath))
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "API 凭据助手缺失，请重新安装完整的应用包。");
        }

        if (target.IsActive && _configService.IsApplied)
        {
            return SwitchResult.Success($"{target.DisplayName} 已经是当前连接。");
        }

        var previousApi = await GetActiveApiProviderAsync(cancellationToken);
        var previousProfile = await _vault.GetActiveProfileAsync(cancellationToken);
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

        if (previousApi is null)
        {
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
        }

        try
        {
            progress?.Report(SwitchStage.ConfiguringProvider);
            var config = await _configService.ApplyAsync(
                target.Definition,
                cancellationToken);
            if (config.Status == DeepSeekConfigChangeStatus.Conflict)
            {
                await RestartIfNeededAsync(wasRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ConfigurationConflict,
                    "Codex 的 API 供应商受管配置已被修改，请恢复配置后重试。");
            }

            await _vault.ClearActiveProfileAsync(cancellationToken);
            await SetApiActiveAsync(ConnectionProvider.DeepSeek,
                provider == ConnectionProvider.DeepSeek,
                cancellationToken);
            await SetApiActiveAsync(ConnectionProvider.Qwen,
                provider == ConnectionProvider.Qwen,
                cancellationToken);

            progress?.Report(SwitchStage.LaunchingChatGpt);
            if (!await _processController.LaunchChatGptAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "ChatGPT did not start after applying the API provider.");
            }

            progress?.Report(SwitchStage.Completed);
            await _logger.InfoAsync(
                "switch.provider",
                $"Activated {target.Definition.ProviderId} Responses provider.");
            return SwitchResult.Success($"已切换到 {target.DisplayName}。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider", exception);
            return await RollBackToPreviousProviderAsync(
                previousApi,
                previousProfile,
                wasRunning);
        }
    }

    private async Task<SwitchResult> SwitchToChatGptFromAnyProviderCoreAsync(
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

        var activeApi = await GetActiveApiProviderAsync(cancellationToken);
        if (activeApi is null && !_configService.IsApplied)
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
            await RestartIfNeededAsync(wasRunning, cancellationToken);
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
        try
        {
            var restore = forceConfigRestore
                ? await _configService.ForceRestoreFromBackupAsync(cancellationToken)
                : await _configService.RestoreAsync(cancellationToken);
            if (restore.Status == DeepSeekConfigChangeStatus.Conflict)
            {
                await RestartIfNeededAsync(wasRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ConfigurationConflict,
                    "API 供应商启用后 Codex 的受管配置发生了修改。可确认使用加密备份恢复，或取消后手动处理。");
            }

            await SetApiActiveAsync(ConnectionProvider.DeepSeek, false, cancellationToken);
            await SetApiActiveAsync(ConnectionProvider.Qwen, false, cancellationToken);
            var result = await _accountSwitchCoordinator.SwitchCoreAsync(
                targetProfileId,
                progress,
                cancellationToken);
            return result.IsSuccess
                ? result
                : await RollBackToPreviousProviderAsync(activeApi, null, wasRunning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.chatgpt", exception);
            return await RollBackToPreviousProviderAsync(activeApi, null, wasRunning);
        }
    }

    private async Task<SwitchResult> ChangeApiModelCoreAsync(
        ConnectionProvider provider,
        string model,
        DateTimeOffset? validatedAt,
        IProgress<SwitchStage>? progress,
        CancellationToken cancellationToken)
    {
        if (_accountSwitchCoordinator.HasPendingTransaction)
        {
            return PendingAccountRecoveryFailure();
        }

        var current = await LoadApiTargetAsync(
            provider,
            validateCredential: false,
            cancellationToken);
        if (current is null)
        {
            return SwitchResult.Failure(SwitchStatus.AuthenticationInvalid, "API 连接不存在。");
        }

        if (!IsModelAvailable(current, model))
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "目标模型不在当前供应商的可用模型列表中。");
        }

        if (string.Equals(current.Definition.Model, model, StringComparison.Ordinal))
        {
            return SwitchResult.Success("该模型已经是当前模型。");
        }

        var next = current with
        {
            Definition = await CreateDefinitionWithModelAsync(
                current,
                model,
                cancellationToken)
        };
        if (!current.IsActive)
        {
            await SaveApiModelAsync(provider, model, validatedAt, cancellationToken);
            return SwitchResult.Success($"已将默认模型设为 {model}。");
        }

        var wasRunning = _processController.IsChatGptRunning();
        progress?.Report(SwitchStage.StoppingChatGpt);
        if (!await _processController.StopChatGptAsync(cancellationToken))
        {
            await RestartIfNeededAsync(wasRunning, cancellationToken);
            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "无法关闭 ChatGPT，请关闭客户端后重试模型切换。");
        }

        var blockers = await FindBlockersAsync(wasRunning, cancellationToken);
        if (blockers is not null)
        {
            return blockers;
        }

        try
        {
            progress?.Report(SwitchStage.ConfiguringProvider);
            var changed = await _configService.ChangeProviderAsync(
                next.Definition,
                cancellationToken);
            if (changed.Status == DeepSeekConfigChangeStatus.Conflict)
            {
                await RestartIfNeededAsync(wasRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ConfigurationConflict,
                    "Codex 的 API 供应商配置已被修改，模型切换已取消。");
            }

            if (changed.Status == DeepSeekConfigChangeStatus.NotApplied)
            {
                throw new InvalidOperationException("API provider configuration is not applied.");
            }

            await SaveApiModelAsync(provider, model, validatedAt, cancellationToken);
            progress?.Report(SwitchStage.LaunchingChatGpt);
            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                throw new InvalidOperationException(
                    "ChatGPT did not restart after changing the API model.");
            }

            progress?.Report(SwitchStage.Completed);
            return SwitchResult.Success($"已切换到 {model}。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.model", exception);
            try
            {
                var restored = await _configService.ChangeProviderAsync(
                    current.Definition,
                    CancellationToken.None);
                if (restored.Status is not (
                        DeepSeekConfigChangeStatus.Applied or
                        DeepSeekConfigChangeStatus.AlreadyApplied))
                {
                    throw new InvalidOperationException(
                        "The previous API model configuration could not be restored.");
                }

                await SaveApiModelAsync(
                    provider,
                    current.Definition.Model,
                    null,
                    CancellationToken.None);
                await RestartIfNeededAsync(wasRunning, CancellationToken.None);
                return SwitchResult.Failure(
                    SwitchStatus.RolledBack,
                    "模型切换失败，已恢复原模型。");
            }
            catch (Exception rollbackException)
            {
                await _logger.ErrorAsync("switch.provider.model.rollback", rollbackException);
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "模型切换和自动恢复均失败，请暂时不要启动 ChatGPT。");
            }
        }
    }

    private async Task<SwitchResult> RollBackToPreviousProviderAsync(
        ConnectionProvider? previousApi,
        AccountProfile? previousProfile,
        bool wasRunning)
    {
        try
        {
            if (!await _processController.StopChatGptAsync(CancellationToken.None))
            {
                throw new InvalidOperationException(
                    "ChatGPT could not be stopped before provider rollback.");
            }

            if (previousApi is { } provider)
            {
                var previous = await LoadApiTargetAsync(
                    provider,
                    validateCredential: false,
                    CancellationToken.None)
                    ?? throw new InvalidOperationException(
                        "The previous API connection is missing during rollback.");
                var reapplied = await _configService.ApplyAsync(
                    previous.Definition,
                    CancellationToken.None);
                if (reapplied.Status is not (
                        DeepSeekConfigChangeStatus.Applied or
                        DeepSeekConfigChangeStatus.AlreadyApplied))
                {
                    throw new InvalidOperationException(
                        "The previous API configuration could not be restored.");
                }

                await _vault.ClearActiveProfileAsync(CancellationToken.None);
                await SetApiActiveAsync(ConnectionProvider.DeepSeek,
                    provider == ConnectionProvider.DeepSeek,
                    CancellationToken.None);
                await SetApiActiveAsync(ConnectionProvider.Qwen,
                    provider == ConnectionProvider.Qwen,
                    CancellationToken.None);
            }
            else
            {
                var restored = await _configService.RestoreAsync(CancellationToken.None);
                if (restored.Status is not (
                        DeepSeekConfigChangeStatus.Restored or
                        DeepSeekConfigChangeStatus.NotApplied))
                {
                    throw new InvalidOperationException(
                        "The original Codex configuration could not be restored.");
                }

                await SetApiActiveAsync(ConnectionProvider.DeepSeek, false, CancellationToken.None);
                await SetApiActiveAsync(ConnectionProvider.Qwen, false, CancellationToken.None);
                if (previousProfile is not null)
                {
                    await _vault.SetActiveProfileAsync(previousProfile.Id, CancellationToken.None);
                }
            }

            if (wasRunning &&
                !await _processController.LaunchChatGptAsync(CancellationToken.None))
            {
                throw new InvalidOperationException("ChatGPT did not start after provider rollback.");
            }

            return SwitchResult.Failure(
                SwitchStatus.RolledBack,
                "切换失败，已恢复原连接。");
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.rollback", exception);
            return SwitchResult.Failure(
                SwitchStatus.Failed,
                "切换和自动恢复均失败，请暂时不要启动 ChatGPT。");
        }
    }

    private async Task<ApiSwitchTarget?> LoadApiTargetAsync(
        ConnectionProvider provider,
        bool validateCredential,
        CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case ConnectionProvider.DeepSeek:
                {
                    var connection = await _deepSeekStore.GetAsync(cancellationToken);
                    var metadata = await _credentialStore.GetMetadataAsync(cancellationToken);
                    if (connection is null || metadata is null)
                    {
                        return null;
                    }

                    if (validateCredential)
                    {
                        _ = await _credentialStore.ReadAsync(cancellationToken);
                    }

                    return new(
                        provider,
                        "DeepSeek",
                        connection.IsActive,
                        ApiProviderDefinitions.ForDeepSeek(connection.Model, connection.Models));
                }
            case ConnectionProvider.Qwen when
                _qwenStore is not null && _qwenCredentialStore is not null:
                {
                    var connection = await _qwenStore.GetAsync(cancellationToken);
                    var metadata = await _qwenCredentialStore.GetMetadataAsync(cancellationToken);
                    if (connection is null || metadata is null)
                    {
                        return null;
                    }

                    if (validateCredential)
                    {
                        _ = await _qwenCredentialStore.ReadAsync(cancellationToken);
                    }

                    return new(
                        provider,
                        "千问 API",
                        connection.IsActive,
                        ApiProviderDefinitions.ForQwen(connection));
                }
            default:
                return null;
        }
    }

    private async Task<ConnectionProvider?> GetActiveApiProviderAsync(
        CancellationToken cancellationToken)
    {
        var deepSeek = await _deepSeekStore.GetAsync(cancellationToken);
        var qwen = _qwenStore is null
            ? null
            : await _qwenStore.GetAsync(cancellationToken);
        if (deepSeek?.IsActive == true && qwen?.IsActive == true)
        {
            throw new InvalidDataException("More than one API provider is marked active.");
        }

        return deepSeek?.IsActive == true
            ? ConnectionProvider.DeepSeek
            : qwen?.IsActive == true
                ? ConnectionProvider.Qwen
                : null;
    }

    private async Task SetApiActiveAsync(
        ConnectionProvider provider,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (provider == ConnectionProvider.DeepSeek)
        {
            await _deepSeekStore.SetActiveAsync(isActive, cancellationToken);
        }
        else if (provider == ConnectionProvider.Qwen && _qwenStore is not null)
        {
            await _qwenStore.SetActiveAsync(isActive, cancellationToken);
        }
    }

    private async Task SaveApiModelAsync(
        ConnectionProvider provider,
        string model,
        DateTimeOffset? validatedAt,
        CancellationToken cancellationToken)
    {
        if (provider == ConnectionProvider.DeepSeek)
        {
            var connection = await _deepSeekStore.GetAsync(cancellationToken)
                ?? throw new InvalidOperationException("DeepSeek connection is missing.");
            await _deepSeekStore.SaveAsync(
                connection with
                {
                    Model = model,
                    LastValidatedAt = validatedAt ?? connection.LastValidatedAt,
                    Status = validatedAt is null ? connection.Status :
                        connection.IsAvailable == false ? DeepSeekConnectionStatus.Unavailable : DeepSeekConnectionStatus.Available,
                    ErrorCode = validatedAt is null ? connection.ErrorCode : null
                },
                cancellationToken: cancellationToken);
            return;
        }

        if (provider == ConnectionProvider.Qwen && _qwenStore is not null)
        {
            var connection = await _qwenStore.GetAsync(cancellationToken)
                ?? throw new InvalidOperationException("Qwen connection is missing.");
            await _qwenStore.SaveAsync(
                connection with
                {
                    Model = model,
                    LastValidatedAt = validatedAt ?? connection.LastValidatedAt,
                    Status = validatedAt is null
                        ? connection.Status
                        : ApiConnectionStatus.Available,
                    ErrorCode = validatedAt is null ? connection.ErrorCode : null
                },
                cancellationToken: cancellationToken);
            return;
        }

        throw new InvalidOperationException("API provider is unsupported.");
    }

    private static bool IsModelAvailable(ApiSwitchTarget target, string model) =>
        target.Definition.Models.Any(item => string.Equals(
                item.Id,
                model,
                StringComparison.Ordinal));

    private async Task<ApiProviderDefinition> CreateDefinitionWithModelAsync(
        ApiSwitchTarget target,
        string model,
        CancellationToken cancellationToken)
    {
        if (target.Provider == ConnectionProvider.DeepSeek)
        {
            var deepSeek = await _deepSeekStore.GetAsync(cancellationToken);
            return ApiProviderDefinitions.ForDeepSeek(model, deepSeek?.Models);
        }

        if (_qwenStore is null)
        {
            throw new InvalidOperationException("Qwen connection store is unavailable.");
        }

        var connection = await _qwenStore.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException("Qwen connection is missing.");
        return ApiProviderDefinitions.ForQwen(connection with { Model = model });
    }

    private sealed record ApiSwitchTarget(
        ConnectionProvider Provider,
        string DisplayName,
        bool IsActive,
        ApiProviderDefinition Definition);

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
            var config = await _configService.ApplyAsync(
                connection.Model,
                cancellationToken);
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
            return SwitchResult.Success("已切换到 DeepSeek 官方 API。");
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

    private async Task<SwitchResult> ChangeDeepSeekModelCoreAsync(
        string model,
        IProgress<SwitchStage>? progress,
        CancellationToken cancellationToken)
    {
        if (_accountSwitchCoordinator.HasPendingTransaction)
        {
            return PendingAccountRecoveryFailure();
        }

        if (!DeepSeekDefaults.IsSupportedModel(model))
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "目标 DeepSeek 模型不受支持。");
        }

        var connection = await _deepSeekStore.GetAsync(cancellationToken);
        if (connection is null)
        {
            return SwitchResult.Failure(
                SwitchStatus.AuthenticationInvalid,
                "DeepSeek 连接不存在。");
        }

        if (string.Equals(connection.Model, model, StringComparison.Ordinal))
        {
            return SwitchResult.Success("该模型已经是当前模型。");
        }

        if (!connection.IsActive && !_configService.IsApplied)
        {
            await _deepSeekStore.SaveAsync(
                connection with { Model = model },
                cancellationToken: cancellationToken);
            return SwitchResult.Success(
                $"已将默认模型设为 {DeepSeekDefaults.GetModelDisplayName(model)}。");
        }

        var wasRunning = _processController.IsChatGptRunning();
        progress?.Report(SwitchStage.StoppingChatGpt);
        if (!await _processController.StopChatGptAsync(cancellationToken))
        {
            await RestartIfNeededAsync(wasRunning, cancellationToken);
            return SwitchResult.Failure(
                SwitchStatus.ProcessBlocked,
                "无法关闭 ChatGPT，请关闭客户端后重试模型切换。");
        }

        var blockers = await FindBlockersAsync(wasRunning, cancellationToken);
        if (blockers is not null)
        {
            return blockers;
        }

        try
        {
            progress?.Report(SwitchStage.ConfiguringProvider);
            var changed = await _configService.ChangeProviderAsync(
                ApiProviderDefinitions.ForDeepSeek(model, connection.Models),
                cancellationToken);
            if (changed.Status == DeepSeekConfigChangeStatus.Conflict)
            {
                await RestartIfNeededAsync(wasRunning, cancellationToken);
                return SwitchResult.Failure(
                    SwitchStatus.ConfigurationConflict,
                    "Codex 配置中的 DeepSeek 受管字段已被修改，模型切换已取消。");
            }

            if (changed.Status == DeepSeekConfigChangeStatus.NotApplied)
            {
                throw new InvalidOperationException(
                    "DeepSeek provider configuration is not applied.");
            }

            await _deepSeekStore.SaveAsync(
                connection with { Model = model },
                cancellationToken: cancellationToken);
            progress?.Report(SwitchStage.LaunchingChatGpt);
            if (!await RestartIfNeededAsync(wasRunning, cancellationToken))
            {
                throw new InvalidOperationException(
                    "ChatGPT did not restart after changing the DeepSeek model.");
            }

            progress?.Report(SwitchStage.Completed);
            return SwitchResult.Success(
                $"已切换到 {DeepSeekDefaults.GetModelDisplayName(model)}。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("switch.provider.model", exception);
            try
            {
                var restored = await _configService.ChangeModelAsync(
                    connection.Model,
                    CancellationToken.None);
                if (restored.Status is not (
                        DeepSeekConfigChangeStatus.Applied or
                        DeepSeekConfigChangeStatus.AlreadyApplied))
                {
                    throw new InvalidOperationException(
                        "The previous DeepSeek model configuration could not be restored.");
                }

                await _deepSeekStore.SaveAsync(
                    connection,
                    cancellationToken: CancellationToken.None);
                if (!await RestartIfNeededAsync(wasRunning, CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "ChatGPT did not restart after restoring the previous model.");
                }

                return SwitchResult.Failure(
                    SwitchStatus.RolledBack,
                    "模型切换失败，已恢复原模型。");
            }
            catch (Exception rollbackException)
            {
                await _logger.ErrorAsync(
                    "switch.provider.model.rollback",
                    rollbackException);
                return SwitchResult.Failure(
                    SwitchStatus.Failed,
                    "模型切换和自动恢复均失败，请暂时不要启动 ChatGPT。");
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

            var connection = await _deepSeekStore.GetAsync(CancellationToken.None)
                ?? throw new InvalidOperationException(
                    "The DeepSeek connection is missing during rollback.");
            var reapplied = await _configService.ApplyAsync(
                connection.Model,
                CancellationToken.None);
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
