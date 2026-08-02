using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GptController.Infrastructure;
using Tomlyn.Parsing;

namespace GptController.Services;

public sealed class DeepSeekCodexConfigService
{
    public const string FlashModel = "deepseek-v4-flash";
    internal const string ProModel = "deepseek-v4-pro";
    public const string ProviderId = "gpt_controller_deepseek";

    private const int StateSchemaVersion = 2;
    private static readonly byte[] BackupEntropy =
        Encoding.UTF8.GetBytes("GptController/DeepSeekCodexConfigBackup/v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ManagedField[] ManagedFields =
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

    private readonly DeepSeekCodexConfigOptions _options;

    public DeepSeekCodexConfigService(DeepSeekCodexConfigOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
    }

    public bool IsApplied => File.Exists(_options.StateFilePath);

    public string CredentialHelperPath => _options.CredentialHelperPath;

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
        var currentHash = HashManagedFields(TomlLineEditor.Parse(current));
        var matchesOriginal = HashEquals(currentHash, state.OriginalManagedHash);
        var matchesApplied = HashEquals(currentHash, state.AppliedManagedHash);

        switch (state.Phase)
        {
            case DeepSeekConfigPhase.Applied when matchesApplied:
                return new(DeepSeekConfigChangeStatus.Applied, state.BackupFilePath);

            case DeepSeekConfigPhase.Applying when matchesApplied:
            case DeepSeekConfigPhase.Restoring when matchesApplied:
                await WriteStateAsync(
                    state with
                    {
                        Phase = DeepSeekConfigPhase.Applied,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
                return new(DeepSeekConfigChangeStatus.Applied, state.BackupFilePath);

            case DeepSeekConfigPhase.Applying when matchesOriginal:
            case DeepSeekConfigPhase.Restoring when matchesOriginal:
                DeleteStateReliably();
                return new(DeepSeekConfigChangeStatus.NotApplied, state.BackupFilePath);

            default:
                return new(DeepSeekConfigChangeStatus.Conflict, state.BackupFilePath);
        }
    }

    public async Task<DeepSeekConfigChangeResult> ApplyAsync(
        CancellationToken cancellationToken = default)
    {
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
            var currentHash = HashManagedFields(currentEditor);
            if (!HashEquals(currentHash, existingState.AppliedManagedHash))
            {
                return new(DeepSeekConfigChangeStatus.Conflict, existingState.BackupFilePath);
            }

            await WriteModelCatalogAsync(cancellationToken);
            return new(DeepSeekConfigChangeStatus.AlreadyApplied, existingState.BackupFilePath);
        }

        var backupPath = await CreateBackupAsync(current, cancellationToken);
        var originalHash = HashManagedFields(currentEditor);
        ApplyManagedValues(currentEditor);
        var updated = currentEditor.Render();
        ValidateToml(updated);
        var appliedHash = HashManagedFields(currentEditor);

        var state = new DeepSeekCodexConfigState(
            StateSchemaVersion,
            DeepSeekConfigPhase.Applying,
            backupPath,
            originalHash,
            appliedHash,
            DateTimeOffset.UtcNow);

        var wroteConfig = false;
        try
        {
            await WriteModelCatalogAsync(cancellationToken);
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
                await AtomicFile.WriteAllTextAsync(_options.ConfigFilePath, current, CancellationToken.None);
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

        return new(DeepSeekConfigChangeStatus.Applied, backupPath);
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
        var restored = RestoreManagedFields(TomlLineEditor.Parse(current), original);
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

    private static string RestoreManagedFields(
        TomlLineEditor currentEditor,
        string original)
    {
        var originalEditor = TomlLineEditor.Parse(original);
        foreach (var field in ManagedFields)
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

        var authSection = $"model_providers.{ProviderId}.auth";
        var providerSection = $"model_providers.{ProviderId}";
        if (!originalEditor.HasSection(authSection))
        {
            currentEditor.RemoveSectionIfEmpty(authSection);
        }

        if (!originalEditor.HasSection(providerSection))
        {
            currentEditor.RemoveSectionIfEmpty(providerSection);
        }

        return currentEditor.Render();
    }

    private void ApplyManagedValues(TomlLineEditor editor)
    {
        editor.Set(string.Empty, "model", Quote(FlashModel));
        editor.Set(string.Empty, "model_provider", Quote(ProviderId));
        editor.Set(string.Empty, "preferred_auth_method", Quote("apikey"));
        editor.Set(string.Empty, "forced_login_method", Quote("api"));
        editor.Set(string.Empty, "model_reasoning_effort", Quote("high"));
        editor.Set(string.Empty, "model_catalog_json", Quote(Path.GetFullPath(_options.ModelCatalogFilePath)));

        var providerSection = $"model_providers.{ProviderId}";
        editor.Set(providerSection, "name", Quote("DeepSeek"));
        editor.Set(providerSection, "base_url", Quote("https://api.deepseek.com/"));
        editor.Set(providerSection, "wire_api", Quote("responses"));
        editor.Remove(providerSection, "env_key");
        editor.Remove(providerSection, "experimental_bearer_token");
        editor.Remove(providerSection, "requires_openai_auth");

        var authSection = $"{providerSection}.auth";
        editor.Set(authSection, "command", Quote(Path.GetFullPath(_options.CredentialHelperPath)));
        editor.Set(authSection, "args", $"[{Quote("get-token")}, {Quote("--provider")}, {Quote("deepseek")}]");
        editor.Set(authSection, "timeout_ms", "5000");
        editor.Set(authSection, "refresh_interval_ms", "0");
    }

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
        if (state is null || state.SchemaVersion != StateSchemaVersion ||
            !Enum.IsDefined(state.Phase) ||
            string.IsNullOrWhiteSpace(state.BackupFilePath) ||
            !IsSha256(state.OriginalManagedHash) ||
            !IsSha256(state.AppliedManagedHash))
        {
            throw new InvalidDataException("The DeepSeek Codex configuration state is invalid.");
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

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

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

    private async Task WriteModelCatalogAsync(CancellationToken cancellationToken)
    {
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
                }
            }
        };

        var json = JsonSerializer.Serialize(catalog, JsonOptions) + Environment.NewLine;
        await AtomicFile.WriteAllTextAsync(_options.ModelCatalogFilePath, json, cancellationToken);
    }

    private static string HashManagedFields(TomlLineEditor editor)
    {
        var builder = new StringBuilder();
        foreach (var field in ManagedFields)
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
        DateTimeOffset UpdatedAtUtc);

    private enum DeepSeekConfigPhase
    {
        Applying,
        Applied,
        Restoring
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
    string? BackupFilePath = null);
