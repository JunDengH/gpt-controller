using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GptController.Infrastructure;
using GptController.Models;
using Tomlyn.Parsing;

namespace GptController.Services;

public sealed class DeepSeekCodexConfigService
{
    public const string FlashModel = DeepSeekDefaults.FlashModel;
    public const string ProModel = DeepSeekDefaults.ProModel;
    public const string ProviderId = ApiProviderDefinitions.DeepSeekProviderId;
    public const string QwenProviderId = ApiProviderDefinitions.QwenProviderId;

    private const int StateSchemaVersion = 4;
    private static readonly byte[] BackupEntropy =
        Encoding.UTF8.GetBytes("GptController/DeepSeekCodexConfigBackup/v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ManagedField[] LegacyManagedFields =
    [
        new(string.Empty, "model"),
        new(string.Empty, "model_provider"),
        new(string.Empty, "preferred_auth_method"),
        new(string.Empty, "forced_login_method"),
        new(string.Empty, "model_reasoning_effort"),
        new(string.Empty, "model_catalog_json"),
        new($"model_providers.{ProviderId}", "name"),
        new($"model_providers.{ProviderId}", "base_url"),
        new($"model_providers.{ProviderId}", "wire_api"),
        new($"model_providers.{ProviderId}", "env_key"),
        new($"model_providers.{ProviderId}", "experimental_bearer_token"),
        new($"model_providers.{ProviderId}", "requires_openai_auth"),
        new($"model_providers.{ProviderId}.auth", "command"),
        new($"model_providers.{ProviderId}.auth", "args"),
        new($"model_providers.{ProviderId}.auth", "timeout_ms"),
        new($"model_providers.{ProviderId}.auth", "refresh_interval_ms")
    ];
    private static readonly ManagedField[] ManagedFields =
    [
        .. LegacyManagedFields,
        new($"model_providers.{QwenProviderId}", "name"),
        new($"model_providers.{QwenProviderId}", "base_url"),
        new($"model_providers.{QwenProviderId}", "wire_api"),
        new($"model_providers.{QwenProviderId}", "env_key"),
        new($"model_providers.{QwenProviderId}", "experimental_bearer_token"),
        new($"model_providers.{QwenProviderId}", "requires_openai_auth"),
        new($"model_providers.{QwenProviderId}.auth", "command"),
        new($"model_providers.{QwenProviderId}.auth", "args"),
        new($"model_providers.{QwenProviderId}.auth", "timeout_ms"),
        new($"model_providers.{QwenProviderId}.auth", "refresh_interval_ms")
    ];

    private readonly DeepSeekCodexConfigOptions _options;

    public DeepSeekCodexConfigService(DeepSeekCodexConfigOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
    }

    public bool IsApplied => File.Exists(_options.StateFilePath);

    public string CredentialHelperPath => _options.CredentialHelperPath;

    internal async Task<bool> HasManagedProviderDependencyAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await ReadConfigAsync(cancellationToken);
        ValidateToml(current);
        var editor = TomlLineEditor.Parse(current);
        var selectedProvider = Unquote(
            editor.GetValue(string.Empty, "model_provider"));
        if (IsManagedProviderId(selectedProvider))
        {
            return true;
        }

        return new[] { ProviderId, QwenProviderId }.Any(providerId =>
            IsCredentialHelperCommand(editor.GetValue(
                $"model_providers.{providerId}.auth",
                "command")));
    }

    public async Task<DeepSeekConfigChangeResult> RecoverInterruptedChangeAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return new(DeepSeekConfigChangeStatus.NotApplied);
        }

        var current = await ReadConfigAsync(cancellationToken);
        ValidateToml(current);
        var currentEditor = TomlLineEditor.Parse(current);
        var fields = state.SchemaVersion < StateSchemaVersion
            ? LegacyManagedFields
            : ManagedFields;
        var currentHash = HashManagedFields(currentEditor, fields);
        var matchesOriginal = HashEquals(currentHash, state.OriginalManagedHash);
        var matchesApplied = HashEquals(currentHash, state.AppliedManagedHash);
        var matchesPreviousApplied =
            state.PreviousAppliedManagedHash is { } previousAppliedHash &&
            HashEquals(currentHash, previousAppliedHash);
        var matchesPendingApplied =
            state.PendingAppliedManagedHash is { } pendingAppliedHash &&
            HashEquals(currentHash, pendingAppliedHash);

        if (state.Phase == DeepSeekConfigPhase.Applied && matchesApplied)
        {
            return await CompleteLegacyStateUpgradeAsync(
                state,
                currentEditor,
                cancellationToken);
        }

        if (state.Phase is DeepSeekConfigPhase.Applying or DeepSeekConfigPhase.Restoring)
        {
            if (matchesApplied)
            {
                var applied = state with
                {
                    Phase = DeepSeekConfigPhase.Applied,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteStateAsync(applied, cancellationToken);
                return await CompleteLegacyStateUpgradeAsync(
                    applied,
                    currentEditor,
                    cancellationToken);
            }

            if (matchesOriginal)
            {
                DeleteStateReliably();
                return new(DeepSeekConfigChangeStatus.NotApplied, state.BackupFilePath);
            }
        }

        if (state.Phase == DeepSeekConfigPhase.Updating &&
            (matchesPendingApplied || matchesPreviousApplied))
        {
            var applied = state with
            {
                Phase = DeepSeekConfigPhase.Applied,
                AppliedManagedHash = matchesPendingApplied
                    ? state.PendingAppliedManagedHash!
                    : state.PreviousAppliedManagedHash!,
                PreviousAppliedManagedHash = null,
                PendingAppliedManagedHash = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await WriteStateAsync(applied, cancellationToken);
            return await CompleteLegacyStateUpgradeAsync(
                applied,
                currentEditor,
                cancellationToken);
        }

        return new(DeepSeekConfigChangeStatus.Conflict, state.BackupFilePath);
    }

    public Task<DeepSeekConfigChangeResult> ApplyAsync(
        CancellationToken cancellationToken = default) =>
        ApplyAsync(ApiProviderDefinitions.ForDeepSeek(FlashModel), cancellationToken);

    public Task<DeepSeekConfigChangeResult> ApplyAsync(
        string model,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(ApiProviderDefinitions.ForDeepSeek(model), cancellationToken);

    public async Task<DeepSeekConfigChangeResult> ApplyAsync(
        ApiProviderDefinition provider,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        var recovery = await RecoverInterruptedChangeAsync(cancellationToken);
        if (recovery.Status == DeepSeekConfigChangeStatus.Conflict)
        {
            return recovery;
        }

        var current = await ReadConfigAsync(cancellationToken);
        ValidateToml(current);
        var currentEditor = TomlLineEditor.Parse(current);
        var existingState = await ReadStateAsync(cancellationToken);
        if (existingState is not null)
        {
            if (!HashEquals(HashManagedFields(currentEditor), existingState.AppliedManagedHash))
            {
                return new(DeepSeekConfigChangeStatus.Conflict, existingState.BackupFilePath);
            }

            if (!IsSelectedProvider(currentEditor, provider))
            {
                return await UpdateAppliedProviderCoreAsync(
                    existingState,
                    currentEditor,
                    provider,
                    cancellationToken);
            }

            await WriteModelCatalogAsync(provider, cancellationToken);
            return new(
                DeepSeekConfigChangeStatus.AlreadyApplied,
                existingState.BackupFilePath,
                provider.ProviderId);
        }

        var backupPath = await CreateBackupAsync(current, cancellationToken);
        var originalHash = HashManagedFields(currentEditor);
        ApplyManagedValues(currentEditor, provider);
        var updated = currentEditor.Render();
        ValidateToml(updated);
        var appliedHash = HashManagedFields(currentEditor);
        var state = new DeepSeekCodexConfigState(
            StateSchemaVersion,
            DeepSeekConfigPhase.Applying,
            backupPath,
            originalHash,
            appliedHash,
            DateTimeOffset.UtcNow,
            ProviderId: provider.ProviderId,
            Model: provider.Model);

        var wroteConfig = false;
        try
        {
            await WriteModelCatalogAsync(provider, cancellationToken);
            await WriteStateAsync(state, cancellationToken);
            await AtomicFile.WriteAllTextAsync(_options.ConfigFilePath, updated, cancellationToken);
            wroteConfig = true;
            await WriteStateAsync(
                state with
                {
                    Phase = DeepSeekConfigPhase.Applied,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        catch
        {
            if (wroteConfig)
            {
                await AtomicFile.WriteAllTextAsync(
                    _options.ConfigFilePath,
                    current,
                    CancellationToken.None);
            }

            if (!wroteConfig || string.Equals(
                    await ReadConfigAsync(CancellationToken.None),
                    current,
                    StringComparison.Ordinal))
            {
                TryDeleteState();
            }

            throw;
        }

        return new(DeepSeekConfigChangeStatus.Applied, backupPath, provider.ProviderId);
    }

    public Task<DeepSeekConfigChangeResult> ChangeModelAsync(
        string model,
        CancellationToken cancellationToken = default) =>
        ChangeProviderAsync(ApiProviderDefinitions.ForDeepSeek(model), cancellationToken);

    public async Task<DeepSeekConfigChangeResult> ChangeProviderAsync(
        ApiProviderDefinition provider,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        var recovery = await RecoverInterruptedChangeAsync(cancellationToken);
        if (recovery.Status == DeepSeekConfigChangeStatus.Conflict)
        {
            return recovery;
        }

        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return new(DeepSeekConfigChangeStatus.NotApplied);
        }

        var current = await ReadConfigAsync(cancellationToken);
        ValidateToml(current);
        var editor = TomlLineEditor.Parse(current);
        if (!HashEquals(HashManagedFields(editor), state.AppliedManagedHash))
        {
            return new(DeepSeekConfigChangeStatus.Conflict, state.BackupFilePath);
        }

        if (IsSelectedProvider(editor, provider))
        {
            await WriteModelCatalogAsync(provider, cancellationToken);
            return new(
                DeepSeekConfigChangeStatus.AlreadyApplied,
                state.BackupFilePath,
                provider.ProviderId);
        }

        return await UpdateAppliedProviderCoreAsync(
            state,
            editor,
            provider,
            cancellationToken);
    }

    public async Task<DeepSeekConfigChangeResult> RestoreAsync(
        CancellationToken cancellationToken = default)
    {
        var recovery = await RecoverInterruptedChangeAsync(cancellationToken);
        if (recovery.Status == DeepSeekConfigChangeStatus.Conflict)
        {
            return recovery;
        }

        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return new(DeepSeekConfigChangeStatus.NotApplied);
        }

        var current = await ReadConfigAsync(cancellationToken);
        ValidateToml(current);
        var currentEditor = TomlLineEditor.Parse(current);
        var currentHash = HashManagedFields(currentEditor);
        if (!HashEquals(currentHash, state.AppliedManagedHash))
        {
            return new(DeepSeekConfigChangeStatus.Conflict, state.BackupFilePath);
        }

        if (!File.Exists(state.BackupFilePath))
        {
            throw new FileNotFoundException("The Codex configuration backup is missing.", state.BackupFilePath);
        }

        var original = await ReadBackupAsync(state.BackupFilePath, cancellationToken);
        ValidateToml(original);
        var restored = RestoreManagedFields(currentEditor, original);
        ValidateToml(restored);
        if (!string.Equals(
                HashManagedFields(TomlLineEditor.Parse(restored)),
                state.OriginalManagedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The restored Codex managed fields do not match the backup.");
        }

        await CreateBackupAsync(current, cancellationToken);
        await WriteStateAsync(
            state with
            {
                Phase = DeepSeekConfigPhase.Restoring,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
        await AtomicFile.WriteAllTextAsync(_options.ConfigFilePath, restored, cancellationToken);
        DeleteStateReliably();
        return new(DeepSeekConfigChangeStatus.Restored, state.BackupFilePath);
    }

    public async Task<DeepSeekConfigChangeResult> ForceRestoreFromBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return new(DeepSeekConfigChangeStatus.NotApplied);
        }

        var current = await ReadConfigAsync(cancellationToken);
        var original = await ReadBackupAsync(state.BackupFilePath, cancellationToken);
        ValidateToml(original);
        ValidateToml(current);
        var restoreFields = state.SchemaVersion < StateSchemaVersion
            ? LegacyManagedFields
            : ManagedFields;
        var restored = RestoreManagedFields(
            TomlLineEditor.Parse(current),
            original,
            restoreFields);
        ValidateToml(restored);
        if (!string.Equals(
                HashManagedFields(TomlLineEditor.Parse(restored), restoreFields),
                state.OriginalManagedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The restored Codex managed fields do not match the backup.");
        }

        await CreateBackupAsync(current, cancellationToken);
        await WriteStateAsync(
            state with
            {
                Phase = DeepSeekConfigPhase.Restoring,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
        await AtomicFile.WriteAllTextAsync(_options.ConfigFilePath, restored, cancellationToken);
        DeleteStateReliably();
        return new(DeepSeekConfigChangeStatus.Restored, state.BackupFilePath);
    }

    private static string RestoreManagedFields(
        TomlLineEditor currentEditor,
        string original,
        IReadOnlyList<ManagedField>? fields = null)
    {
        fields ??= ManagedFields;
        var originalEditor = TomlLineEditor.Parse(original);
        foreach (var field in fields)
        {
            var originalValue = originalEditor.GetValue(field.Section, field.Key);
            if (originalValue is null)
            {
                currentEditor.Remove(field.Section, field.Key);
            }
            else
            {
                currentEditor.Set(field.Section, field.Key, originalValue);
            }
        }

        foreach (var managedProviderId in new[] { ProviderId, QwenProviderId }.Where(id =>
                     fields.Any(field => field.Section.StartsWith(
                         $"model_providers.{id}",
                         StringComparison.Ordinal))))
        {
            var authSection = $"model_providers.{managedProviderId}.auth";
            var providerSection = $"model_providers.{managedProviderId}";
            if (!originalEditor.HasSection(authSection))
            {
                currentEditor.RemoveSectionIfEmpty(authSection);
            }

            if (!originalEditor.HasSection(providerSection))
            {
                currentEditor.RemoveSectionIfEmpty(providerSection);
            }
        }

        return currentEditor.Render();
    }

    private async Task<DeepSeekConfigChangeResult> UpdateAppliedProviderCoreAsync(
        DeepSeekCodexConfigState state,
        TomlLineEditor currentEditor,
        ApiProviderDefinition provider,
        CancellationToken cancellationToken)
    {
        var original = currentEditor.Render();
        var previousHash = state.AppliedManagedHash;
        ApplyManagedValues(currentEditor, provider);
        var updated = currentEditor.Render();
        ValidateToml(updated);
        var pendingHash = HashManagedFields(currentEditor);
        var updatingState = state with
        {
            SchemaVersion = StateSchemaVersion,
            Phase = DeepSeekConfigPhase.Updating,
            PreviousAppliedManagedHash = previousHash,
            PendingAppliedManagedHash = pendingHash,
            ProviderId = provider.ProviderId,
            Model = provider.Model,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var wroteConfig = false;
        try
        {
            await WriteModelCatalogAsync(provider, cancellationToken);
            await WriteStateAsync(updatingState, cancellationToken);
            await AtomicFile.WriteAllTextAsync(
                _options.ConfigFilePath,
                updated,
                cancellationToken);
            wroteConfig = true;
            await WriteStateAsync(
                updatingState with
                {
                    Phase = DeepSeekConfigPhase.Applied,
                    AppliedManagedHash = pendingHash,
                    PreviousAppliedManagedHash = null,
                    PendingAppliedManagedHash = null,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        catch
        {
            if (wroteConfig)
            {
                try
                {
                    await AtomicFile.WriteAllTextAsync(
                        _options.ConfigFilePath,
                        original,
                        CancellationToken.None);
                    await WriteStateAsync(
                        state with
                        {
                            SchemaVersion = StateSchemaVersion,
                            Phase = DeepSeekConfigPhase.Applied,
                            PreviousAppliedManagedHash = null,
                            PendingAppliedManagedHash = null,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        },
                        CancellationToken.None);
                }
                catch
                {
                    // The updating marker lets startup recovery identify the
                    // complete managed-field generation left on disk.
                }
            }

            throw;
        }

        return new(
            DeepSeekConfigChangeStatus.Applied,
            state.BackupFilePath,
            provider.ProviderId);
    }

    private void ApplyManagedValues(
        TomlLineEditor editor,
        ApiProviderDefinition provider)
    {
        editor.Set(string.Empty, "model", Quote(provider.Model));
        editor.Set(string.Empty, "model_provider", Quote(provider.ProviderId));
        editor.Set(string.Empty, "preferred_auth_method", Quote("apikey"));
        editor.Set(string.Empty, "forced_login_method", Quote("api"));
        if (provider.SupportsReasoning)
        {
            editor.Set(string.Empty, "model_reasoning_effort", Quote("high"));
        }
        else
        {
            editor.Remove(string.Empty, "model_reasoning_effort");
        }

        editor.Set(
            string.Empty,
            "model_catalog_json",
            Quote(Path.GetFullPath(_options.ModelCatalogFilePath)));

        var inactiveProviderId = string.Equals(
            provider.ProviderId,
            ProviderId,
            StringComparison.Ordinal)
            ? QwenProviderId
            : ProviderId;
        RemoveProviderValues(editor, inactiveProviderId);

        var providerSection = $"model_providers.{provider.ProviderId}";
        editor.Set(providerSection, "name", Quote(provider.DisplayName));
        editor.Set(providerSection, "base_url", Quote(provider.BaseUrl.AbsoluteUri));
        editor.Set(providerSection, "wire_api", Quote("responses"));
        editor.Remove(providerSection, "env_key");
        editor.Remove(providerSection, "experimental_bearer_token");
        editor.Remove(providerSection, "requires_openai_auth");

        var authSection = $"{providerSection}.auth";
        editor.Set(authSection, "command", Quote(Path.GetFullPath(_options.CredentialHelperPath)));
        editor.Set(
            authSection,
            "args",
            $"[{Quote("get-token")}, {Quote("--provider")}, {Quote(provider.CredentialProvider)}]");
        editor.Set(authSection, "timeout_ms", "5000");
        editor.Set(authSection, "refresh_interval_ms", "0");
    }

    private static void RemoveProviderValues(TomlLineEditor editor, string providerId)
    {
        foreach (var field in ManagedFields.Where(item =>
                     item.Section.StartsWith(
                         $"model_providers.{providerId}",
                         StringComparison.Ordinal)))
        {
            editor.Remove(field.Section, field.Key);
        }

        editor.RemoveSectionIfEmpty($"model_providers.{providerId}.auth");
        editor.RemoveSectionIfEmpty($"model_providers.{providerId}");
    }

    private static bool IsSelectedProvider(
        TomlLineEditor editor,
        ApiProviderDefinition provider) =>
        string.Equals(
            editor.GetValue(string.Empty, "model"),
            Quote(provider.Model),
            StringComparison.Ordinal) &&
        string.Equals(
            editor.GetValue(string.Empty, "model_provider"),
            Quote(provider.ProviderId),
            StringComparison.Ordinal) &&
        string.Equals(
            editor.GetValue($"model_providers.{provider.ProviderId}", "base_url"),
            Quote(provider.BaseUrl.AbsoluteUri),
            StringComparison.Ordinal);

    private async Task<string> ReadConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ConfigFilePath))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(_options.ConfigFilePath, cancellationToken);
    }

    private async Task<DeepSeekCodexConfigState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.StateFilePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_options.StateFilePath);
        var state = await JsonSerializer.DeserializeAsync<DeepSeekCodexConfigState>(
            stream,
            JsonOptions,
            cancellationToken);
        if (state is null || state.SchemaVersion is < 2 or > StateSchemaVersion ||
            !Enum.IsDefined(state.Phase) ||
            string.IsNullOrWhiteSpace(state.BackupFilePath) ||
            !IsSha256(state.OriginalManagedHash) ||
            !IsSha256(state.AppliedManagedHash) ||
            (state.SchemaVersion >= StateSchemaVersion &&
             (!IsManagedProviderId(state.ProviderId) || string.IsNullOrWhiteSpace(state.Model))) ||
            (state.Phase == DeepSeekConfigPhase.Updating &&
             (!IsSha256(state.PreviousAppliedManagedHash) ||
              !IsSha256(state.PendingAppliedManagedHash))))
        {
            throw new InvalidDataException("The Codex API provider configuration state is invalid.");
        }

        return state;
    }

    private Task WriteStateAsync(
        DeepSeekCodexConfigState state,
        CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(
            _options.StateFilePath,
            JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine,
            cancellationToken);

    private void DeleteStateReliably()
    {
        File.Delete(_options.StateFilePath);
        if (File.Exists(_options.StateFilePath))
        {
            throw new IOException("The DeepSeek Codex configuration state could not be removed.");
        }
    }

    private void TryDeleteState()
    {
        try
        {
            DeleteStateReliably();
        }
        catch
        {
            // A retained phase marker lets startup recovery finish safely.
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void ValidateProvider(ApiProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!IsManagedProviderId(provider.ProviderId) ||
            provider.Provider is not (ConnectionProvider.DeepSeek or ConnectionProvider.Qwen) ||
            string.IsNullOrWhiteSpace(provider.Model) ||
            provider.BaseUrl.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(provider.CredentialProvider) ||
            !provider.Models.Any(item => string.Equals(
                item.Id,
                provider.Model,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException("The API provider definition is invalid.", nameof(provider));
        }

        if (provider.Provider == ConnectionProvider.DeepSeek &&
            !DeepSeekDefaults.IsSupportedModel(provider.Model))
        {
            throw new ArgumentException("DeepSeek model is unsupported.", nameof(provider));
        }

        if (provider.Provider == ConnectionProvider.Qwen &&
            !provider.Model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Qwen model is unsupported.", nameof(provider));
        }
    }

    private static bool IsManagedProviderId(string? providerId) =>
        string.Equals(providerId, ProviderId, StringComparison.Ordinal) ||
        string.Equals(providerId, QwenProviderId, StringComparison.Ordinal);

    private static bool HashEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private async Task<string> CreateBackupAsync(
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.ConfigFilePath)
            ?? throw new InvalidOperationException("The Codex config path has no directory.");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_options.ConfigFilePath)}.gpt-controller-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak.dpapi");
        var clear = Encoding.UTF8.GetBytes(content);
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(
                clear,
                BackupEntropy,
                DataProtectionScope.CurrentUser);
            await AtomicFile.WriteAllBytesAsync(backupPath, encrypted, cancellationToken);
            return backupPath;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    private static async Task<string> ReadBackupAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        var encrypted = await File.ReadAllBytesAsync(backupPath, cancellationToken);
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(
                encrypted,
                BackupEntropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The Codex configuration backup cannot be decrypted for the current Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    private async Task<DeepSeekConfigChangeResult> CompleteLegacyStateUpgradeAsync(
        DeepSeekCodexConfigState state,
        TomlLineEditor currentEditor,
        CancellationToken cancellationToken)
    {
        if (state.SchemaVersion >= StateSchemaVersion)
        {
            return new(
                DeepSeekConfigChangeStatus.Applied,
                state.BackupFilePath,
                state.ProviderId);
        }

        var original = await ReadBackupAsync(state.BackupFilePath, cancellationToken);
        ValidateToml(original);
        var model = Unquote(currentEditor.GetValue(string.Empty, "model"))
            ?? DeepSeekDefaults.Model;
        var upgraded = state with
        {
            SchemaVersion = StateSchemaVersion,
            Phase = DeepSeekConfigPhase.Applied,
            OriginalManagedHash = HashManagedFields(TomlLineEditor.Parse(original)),
            AppliedManagedHash = HashManagedFields(currentEditor),
            PreviousAppliedManagedHash = null,
            PendingAppliedManagedHash = null,
            ProviderId = ProviderId,
            Model = model,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await WriteStateAsync(upgraded, cancellationToken);
        return new(
            DeepSeekConfigChangeStatus.Applied,
            upgraded.BackupFilePath,
            upgraded.ProviderId);
    }

    private static string? Unquote(string? value)
    {
        if (value is not { Length: >= 2 } || value[0] != '"' || value[^1] != '"')
        {
            return null;
        }

        return value[1..^1];
    }

    private static bool IsCredentialHelperCommand(string? tomlValue)
    {
        if (!TryDecodeTomlString(tomlValue, out var command) ||
            !Path.IsPathFullyQualified(command))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileName(command),
            CredentialHelperLocator.ExecutableName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDecodeTomlString(
        string? tomlValue,
        out string decoded)
    {
        decoded = string.Empty;
        if (tomlValue is not { Length: >= 2 })
        {
            return false;
        }

        if (tomlValue[0] == '\'' && tomlValue[^1] == '\'')
        {
            decoded = tomlValue[1..^1];
            return true;
        }

        if (tomlValue[0] != '"' || tomlValue[^1] != '"')
        {
            return false;
        }

        try
        {
            decoded = JsonSerializer.Deserialize<string>(tomlValue) ?? string.Empty;
            return decoded.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task WriteModelCatalogAsync(
        ApiProviderDefinition provider,
        CancellationToken cancellationToken)
    {
        if (provider.Provider == ConnectionProvider.Qwen)
        {
            var models = provider.Models
                .DistinctBy(item => item.Id, StringComparer.Ordinal)
                .Select((item, index) => (object)new
                {
                    slug = item.Id,
                    prefer_websockets = false,
                    support_verbosity = false,
                    default_verbosity = "low",
                    apply_patch_tool_type = "function",
                    web_search_tool_type = "text",
                    input_modalities = new[] { "text" },
                    supports_image_detail_original = false,
                    truncation_policy = new { mode = "tokens", limit = 8_000 },
                    supports_parallel_tool_calls = false,
                    multi_agent_version = "v2",
                    use_responses_lite = false,
                    include_skills_usage_instructions = false,
                    context_window = provider.ConservativeContextWindow,
                    max_context_window = provider.ConservativeContextWindow,
                    effective_context_window_percent = 90,
                    comp_hash = "3000",
                    reasoning_summary_format = "experimental",
                    default_reasoning_summary = "none",
                    display_name = item.EffectiveDisplayName,
                    description = "Qwen model discovered from Alibaba Cloud Model Studio.",
                    default_reasoning_level = "medium",
                    supported_reasoning_levels = new object[]
                    {
                        new { effort = "medium", description = "Provider-compatible default" }
                    },
                    base_instructions = "You are Codex, an agentic coding assistant. Work carefully in the user's repository, use function tools when needed, preserve unrelated changes, and verify your work.",
                    shell_type = "shell_command",
                    visibility = "list",
                    minimal_client_version = "0.146.0",
                    supported_in_api = true,
                    priority = index + 1,
                    experimental_supported_tools = Array.Empty<string>(),
                    supports_search_tool = false,
                    supports_reasoning_summaries = false
                })
                .ToArray();
            var qwenJson = JsonSerializer.Serialize(new { models }, JsonOptions) +
                           Environment.NewLine;
            await AtomicFile.WriteAllTextAsync(
                _options.ModelCatalogFilePath,
                qwenJson,
                cancellationToken);
            return;
        }

        var catalog = new
        {
            models = new object[]
            {
                new
                {
                    slug = FlashModel,
                    prefer_websockets = false,
                    support_verbosity = true,
                    default_verbosity = "low",
                    apply_patch_tool_type = "freeform",
                    web_search_tool_type = "text",
                    input_modalities = new[] { "text" },
                    supports_image_detail_original = false,
                    truncation_policy = new { mode = "tokens", limit = 10_000 },
                    supports_parallel_tool_calls = true,
                    multi_agent_version = "v2",
                    use_responses_lite = false,
                    include_skills_usage_instructions = false,
                    context_window = 1_048_576,
                    max_context_window = 1_048_576,
                    effective_context_window_percent = 95,
                    comp_hash = "3000",
                    reasoning_summary_format = "experimental",
                    default_reasoning_summary = "none",
                    display_name = "DeepSeek-V4-Flash",
                    description = "Latest frontier agentic coding model.",
                    default_reasoning_level = "high",
                    supported_reasoning_levels = new object[]
                    {
                        new { effort = "low", description = "Fast responses with lighter reasoning" },
                        new { effort = "high", description = "Extra high reasoning depth for complex problems" },
                        new { effort = "max", description = "Maximum reasoning depth for the hardest problems" }
                    },
                    base_instructions = "You are Codex, an agentic coding assistant. Work carefully in the user's repository, use tools when needed, preserve unrelated changes, and verify your work.",
                    shell_type = "shell_command",
                    visibility = "list",
                    minimal_client_version = "0.146.0",
                    supported_in_api = true,
                    priority = 1,
                    experimental_supported_tools = Array.Empty<string>(),
                    supports_search_tool = true,
                    supports_reasoning_summaries = true
                },
                new
                {
                    slug = ProModel,
                    prefer_websockets = false,
                    support_verbosity = true,
                    default_verbosity = "low",
                    apply_patch_tool_type = "freeform",
                    web_search_tool_type = "text",
                    input_modalities = new[] { "text" },
                    supports_image_detail_original = false,
                    truncation_policy = new { mode = "tokens", limit = 10_000 },
                    supports_parallel_tool_calls = true,
                    multi_agent_version = "v2",
                    use_responses_lite = false,
                    include_skills_usage_instructions = false,
                    context_window = 1_048_576,
                    max_context_window = 1_048_576,
                    effective_context_window_percent = 95,
                    comp_hash = "3000",
                    reasoning_summary_format = "experimental",
                    default_reasoning_summary = "none",
                    display_name = "DeepSeek-V4-Pro",
                    description = "Higher-capability reasoning and agentic coding model.",
                    default_reasoning_level = "high",
                    supported_reasoning_levels = new object[]
                    {
                        new { effort = "low", description = "Fast responses with lighter reasoning" },
                        new { effort = "high", description = "Extra high reasoning depth for complex problems" },
                        new { effort = "max", description = "Maximum reasoning depth for the hardest problems" }
                    },
                    base_instructions = "You are Codex, an agentic coding assistant. Work carefully in the user's repository, use tools when needed, preserve unrelated changes, and verify your work.",
                    shell_type = "shell_command",
                    visibility = "list",
                    minimal_client_version = "0.146.0",
                    supported_in_api = true,
                    priority = 2,
                    experimental_supported_tools = Array.Empty<string>(),
                    supports_search_tool = true,
                    supports_reasoning_summaries = true
                }
            }
        };

        var json = JsonSerializer.Serialize(catalog, JsonOptions) + Environment.NewLine;
        await AtomicFile.WriteAllTextAsync(_options.ModelCatalogFilePath, json, cancellationToken);
    }

    private static string HashManagedFields(TomlLineEditor editor) =>
        HashManagedFields(editor, ManagedFields);

    private static string HashManagedFields(
        TomlLineEditor editor,
        IReadOnlyList<ManagedField> fields)
    {
        var builder = new StringBuilder();
        foreach (var field in fields)
        {
            builder.Append(field.Section)
                .Append('\u001f')
                .Append(field.Key)
                .Append('\u001f')
                .Append(editor.GetValue(field.Section, field.Key) ?? "<absent>")
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void ValidateToml(string content)
    {
        var result = SyntaxParser.Parse(content, validate: true);
        if (result.HasErrors)
        {
            throw new InvalidDataException(
                "Codex config.toml is invalid: " + string.Join("; ", result.Diagnostics));
        }
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                _ when char.IsControl(character) => $"\\u{(int)character:X4}",
                _ => character.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    private sealed record ManagedField(string Section, string Key);

    private sealed record DeepSeekCodexConfigState(
        int SchemaVersion,
        DeepSeekConfigPhase Phase,
        string BackupFilePath,
        string OriginalManagedHash,
        string AppliedManagedHash,
        DateTimeOffset UpdatedAtUtc,
        string? PreviousAppliedManagedHash = null,
        string? PendingAppliedManagedHash = null,
        string? ProviderId = null,
        string? Model = null);

    private enum DeepSeekConfigPhase
    {
        Applying,
        Applied,
        Restoring,
        Updating
    }

    private sealed class TomlLineEditor
    {
        private static readonly Regex TableRegex = new(
            @"^\s*\[(?<section>[^\]]+)\]\s*(?:#.*)?$",
            RegexOptions.CultureInvariant);

        private readonly List<string> _lines;
        private readonly string _newLine;

        private TomlLineEditor(List<string> lines, string newLine)
        {
            _lines = lines;
            _newLine = newLine;
        }

        public static TomlLineEditor Parse(string content)
        {
            var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = normalized.Split('\n').ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return new(lines, newLine);
        }

        public string? GetValue(string section, string key)
        {
            var index = FindAssignment(section, key);
            return index < 0 ? null : ExtractValue(_lines[index], key);
        }

        public bool HasSection(string section) => FindSection(section) is not null;

        public void Set(string section, string key, string value)
        {
            var assignmentIndex = FindAssignment(section, key);
            if (assignmentIndex >= 0)
            {
                var line = _lines[assignmentIndex];
                var match = AssignmentRegex(key).Match(line);
                var comment = SplitValueAndComment(match.Groups["value"].Value).Comment;
                _lines[assignmentIndex] = match.Groups["prefix"].Value + value + comment;
                return;
            }

            if (section.Length == 0)
            {
                var firstTable = FindFirstTable();
                var insertAt = firstTable < 0 ? _lines.Count : firstTable;
                _lines.Insert(insertAt, $"{key} = {value}");
                return;
            }

            var sectionRange = FindSection(section);
            if (sectionRange is null)
            {
                if (_lines.Count > 0 && _lines[^1].Length != 0)
                {
                    _lines.Add(string.Empty);
                }

                _lines.Add($"[{section}]");
                _lines.Add($"{key} = {value}");
                return;
            }

            _lines.Insert(sectionRange.Value.EndExclusive, $"{key} = {value}");
        }

        public void Remove(string section, string key)
        {
            var assignmentIndex = FindAssignment(section, key);
            if (assignmentIndex >= 0)
            {
                _lines.RemoveAt(assignmentIndex);
            }
        }

        public void RemoveSectionIfEmpty(string section)
        {
            var range = FindSection(section);
            if (range is null)
            {
                return;
            }

            for (var index = range.Value.Header + 1; index < range.Value.EndExclusive; index++)
            {
                var trimmed = _lines[index].TrimStart();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    return;
                }
            }

            var removeFrom = range.Value.Header;
            if (removeFrom > 0 && string.IsNullOrWhiteSpace(_lines[removeFrom - 1]))
            {
                removeFrom--;
            }

            _lines.RemoveRange(removeFrom, range.Value.EndExclusive - removeFrom);
        }

        public string Render()
        {
            return _lines.Count == 0
                ? string.Empty
                : string.Join(_newLine, _lines).TrimEnd('\r', '\n') + _newLine;
        }

        private int FindAssignment(string section, string key)
        {
            var currentSection = string.Empty;
            for (var index = 0; index < _lines.Count; index++)
            {
                var table = TableRegex.Match(_lines[index]);
                if (table.Success)
                {
                    currentSection = table.Groups["section"].Value.Trim();
                    continue;
                }

                if (string.Equals(currentSection, section, StringComparison.Ordinal) &&
                    AssignmentRegex(key).IsMatch(_lines[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private (int Header, int EndExclusive)? FindSection(string section)
        {
            for (var index = 0; index < _lines.Count; index++)
            {
                var table = TableRegex.Match(_lines[index]);
                if (!table.Success ||
                    !string.Equals(table.Groups["section"].Value.Trim(), section, StringComparison.Ordinal))
                {
                    continue;
                }

                var end = index + 1;
                while (end < _lines.Count && !TableRegex.IsMatch(_lines[end]))
                {
                    end++;
                }

                return (index, end);
            }

            return null;
        }

        private int FindFirstTable()
        {
            for (var index = 0; index < _lines.Count; index++)
            {
                if (TableRegex.IsMatch(_lines[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private static string ExtractValue(string line, string key)
        {
            var match = AssignmentRegex(key).Match(line);
            return SplitValueAndComment(match.Groups["value"].Value).Value.Trim();
        }

        private static (string Value, string Comment) SplitValueAndComment(string input)
        {
            var inBasicString = false;
            var inLiteralString = false;
            var escaped = false;
            for (var index = 0; index < input.Length; index++)
            {
                var character = input[index];
                if (inBasicString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inBasicString = false;
                    }

                    continue;
                }

                if (inLiteralString)
                {
                    if (character == '\'')
                    {
                        inLiteralString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inBasicString = true;
                }
                else if (character == '\'')
                {
                    inLiteralString = true;
                }
                else if (character == '#')
                {
                    var commentStart = index;
                    while (commentStart > 0 && char.IsWhiteSpace(input[commentStart - 1]))
                    {
                        commentStart--;
                    }

                    return (input[..commentStart], input[commentStart..]);
                }
            }

            return (input, string.Empty);
        }

        private static Regex AssignmentRegex(string key) => new(
            $@"^(?<prefix>\s*{Regex.Escape(key)}\s*=\s*)(?<value>.*)$",
            RegexOptions.CultureInvariant);
    }
}

public sealed record DeepSeekCodexConfigOptions(
    string ConfigFilePath,
    string ModelCatalogFilePath,
    string StateFilePath,
    string CredentialHelperPath)
{
    internal DeepSeekCodexConfigOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfigFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelCatalogFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(StateFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(CredentialHelperPath);
        return this with
        {
            ConfigFilePath = Path.GetFullPath(ConfigFilePath),
            ModelCatalogFilePath = Path.GetFullPath(ModelCatalogFilePath),
            StateFilePath = Path.GetFullPath(StateFilePath),
            CredentialHelperPath = Path.GetFullPath(CredentialHelperPath)
        };
    }
}

public enum DeepSeekConfigChangeStatus
{
    Applied,
    AlreadyApplied,
    Restored,
    NotApplied,
    Conflict
}

public sealed record DeepSeekConfigChangeResult(
    DeepSeekConfigChangeStatus Status,
    string? BackupFilePath = null,
    string? ProviderId = null);
