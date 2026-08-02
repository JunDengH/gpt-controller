using System.Text.Json;
using System.Text.Json.Nodes;
using GptController.Services;
using Tomlyn.Parsing;

namespace GptController.Tests;

public sealed class DeepSeekCodexConfigServiceTests
{
    [Fact]
    public async Task Apply_PreservesUnrelatedContentAndRemovesPlaintextProviderToken()
    {
        using var fixture = new ConfigFixture();
        await File.WriteAllTextAsync(
            fixture.ConfigPath,
            """
            # user heading
            model = "gpt-5" # keep this comment
            custom_root = "untouched"

            [mcp_servers.example]
            command = "example-mcp"

            [model_providers.gpt_controller_deepseek]
            experimental_bearer_token = "sk-plain-must-not-remain"
            custom_provider_value = "preserved"
            """ + Environment.NewLine);

        var result = await fixture.Service.ApplyAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Applied, result.Status);
        var updated = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("# user heading", updated);
        Assert.Contains("model = \"deepseek-v4-flash\" # keep this comment", updated);
        Assert.Contains("custom_root = \"untouched\"", updated);
        Assert.Contains("[mcp_servers.example]", updated);
        Assert.Contains("custom_provider_value = \"preserved\"", updated);
        Assert.DoesNotContain("sk-plain-must-not-remain", updated);
        Assert.DoesNotContain("experimental_bearer_token", updated);
        Assert.Contains("[model_providers.gpt_controller_deepseek.auth]", updated);
        Assert.Contains("args = [\"get-token\", \"--provider\", \"deepseek\"]", updated);
        Assert.False(SyntaxParser.Parse(updated, validate: true).HasErrors);
        Assert.True(File.Exists(result.BackupFilePath));
        Assert.DoesNotContain(
            "sk-plain-must-not-remain",
            await File.ReadAllTextAsync(result.BackupFilePath!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_IsIdempotentAndDoesNotCreateAnotherBackup()
    {
        using var fixture = new ConfigFixture();
        await File.WriteAllTextAsync(fixture.ConfigPath, "custom = 42" + Environment.NewLine);

        var first = await fixture.Service.ApplyAsync();
        var afterFirst = await File.ReadAllTextAsync(fixture.ConfigPath);
        var backupCount = Directory.GetFiles(fixture.Root, "*.bak.dpapi").Length;
        var second = await fixture.Service.ApplyAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Applied, first.Status);
        Assert.Equal(DeepSeekConfigChangeStatus.AlreadyApplied, second.Status);
        Assert.Equal(afterFirst, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.Equal(backupCount, Directory.GetFiles(fixture.Root, "*.bak.dpapi").Length);
        Assert.Equal(first.BackupFilePath, second.BackupFilePath);
    }

    [Fact]
    public async Task Apply_EscapesWindowsPathsAndWritesOnlyFlashToCatalog()
    {
        using var fixture = new ConfigFixture(
            modelCatalogRelativePath: @"catalog folder\models.json",
            helperRelativePath: @"bin folder\GptController.CredentialHelper.exe");

        await fixture.Service.ApplyAsync();

        var config = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("catalog folder\\\\models.json", config);
        Assert.Contains("bin folder\\\\GptController.CredentialHelper.exe", config);
        Assert.Equal(Path.GetFullPath(fixture.HelperPath), fixture.Service.CredentialHelperPath);
        Assert.False(SyntaxParser.Parse(config, validate: true).HasErrors);

        var catalog = await File.ReadAllTextAsync(fixture.ModelCatalogPath);
        using var json = JsonDocument.Parse(catalog);
        var models = json.RootElement.GetProperty("models");
        Assert.Single(models.EnumerateArray());
        Assert.Equal(
            DeepSeekCodexConfigService.FlashModel,
            models[0].GetProperty("slug").GetString());
        Assert.DoesNotContain("deepseek-v4-pro", catalog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_RestoresManagedValuesAndKeepsLaterUnrelatedChanges()
    {
        using var fixture = new ConfigFixture();
        var original =
            """
            model = "gpt-5"
            model_provider = "openai"
            custom_root = "before"

            [model_providers.gpt_controller_deepseek]
            experimental_bearer_token = "sk-original"
            """ + Environment.NewLine;
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        await fixture.Service.ApplyAsync();

        var applied = await File.ReadAllTextAsync(fixture.ConfigPath);
        applied += Environment.NewLine + "[mcp_servers.added_later]" + Environment.NewLine +
                   "command = \"keep-me\"" + Environment.NewLine;
        await File.WriteAllTextAsync(fixture.ConfigPath, applied);

        var result = await fixture.Service.RestoreAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Restored, result.Status);
        var restored = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("model = \"gpt-5\"", restored);
        Assert.Contains("model_provider = \"openai\"", restored);
        Assert.Contains("custom_root = \"before\"", restored);
        Assert.Contains("experimental_bearer_token = \"sk-original\"", restored);
        Assert.Contains("[mcp_servers.added_later]", restored);
        Assert.Contains("command = \"keep-me\"", restored);
        Assert.DoesNotContain("model_reasoning_effort", restored);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(SyntaxParser.Parse(restored, validate: true).HasErrors);
    }

    [Fact]
    public async Task Restore_WhenManagedFieldChanged_ReturnsConflictWithoutWriting()
    {
        using var fixture = new ConfigFixture();
        await fixture.Service.ApplyAsync();
        var changed = (await File.ReadAllTextAsync(fixture.ConfigPath))
            .Replace("model_reasoning_effort = \"high\"", "model_reasoning_effort = \"low\"");
        await File.WriteAllTextAsync(fixture.ConfigPath, changed);

        var result = await fixture.Service.RestoreAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Conflict, result.Status);
        Assert.Equal(changed, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.True(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task ForceRestore_ReplacesConflictWithEncryptedOriginalBackup()
    {
        using var fixture = new ConfigFixture();
        const string original = "model = \"gpt-original\"\ncustom = 1\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        await fixture.Service.ApplyAsync();
        var changed = (await File.ReadAllTextAsync(fixture.ConfigPath))
            .Replace("model_reasoning_effort = \"high\"", "model_reasoning_effort = \"low\"");
        await File.WriteAllTextAsync(fixture.ConfigPath, changed);

        var result = await fixture.Service.ForceRestoreFromBackupAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Restored, result.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task ForceRestore_PreservesUnrelatedChangesMadeAfterApply()
    {
        using var fixture = new ConfigFixture();
        const string original = "model = \"gpt-original\"\ncustom_root = \"keep\"\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        await fixture.Service.ApplyAsync();
        var changed = (await File.ReadAllTextAsync(fixture.ConfigPath))
            .Replace("model_reasoning_effort = \"high\"", "model_reasoning_effort = \"low\"") +
            "\n[mcp_servers.new]\ncommand = \"keep-new-mcp\"\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, changed);

        var result = await fixture.Service.ForceRestoreFromBackupAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Restored, result.Status);
        var restored = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("model = \"gpt-original\"", restored);
        Assert.Contains("custom_root = \"keep\"", restored);
        Assert.Contains("[mcp_servers.new]", restored);
        Assert.Contains("command = \"keep-new-mcp\"", restored);
        Assert.DoesNotContain("model_reasoning_effort", restored);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(SyntaxParser.Parse(restored, validate: true).HasErrors);
    }

    [Fact]
    public async Task RecoverInterruptedApply_RollsForwardWhenConfigWasWritten()
    {
        using var fixture = new ConfigFixture();
        const string original = "model = \"gpt-original\"\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        await fixture.Service.ApplyAsync();
        var applied = await File.ReadAllTextAsync(fixture.ConfigPath);
        await SetStatePhaseAsync(fixture.StatePath, "applying");

        var result = await fixture.Service.RecoverInterruptedChangeAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Applied, result.Status);
        Assert.Equal(applied, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.Equal("applied", await ReadStatePhaseAsync(fixture.StatePath));
    }

    [Fact]
    public async Task RecoverInterruptedApply_RollsBackMarkerWhenConfigWasNotWritten()
    {
        using var fixture = new ConfigFixture();
        const string original = "model = \"gpt-original\"\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        await fixture.Service.ApplyAsync();
        await SetStatePhaseAsync(fixture.StatePath, "applying");
        await File.WriteAllTextAsync(fixture.ConfigPath, original);

        var result = await fixture.Service.RecoverInterruptedChangeAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.NotApplied, result.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task RecoverInterruptedRestore_CompletesWhenOriginalConfigWasWritten()
    {
        using var fixture = new ConfigFixture();
        const string original = "model = \"gpt-original\"\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        await fixture.Service.ApplyAsync();
        var interruptedState = await File.ReadAllTextAsync(fixture.StatePath);
        await fixture.Service.RestoreAsync();
        await File.WriteAllTextAsync(fixture.StatePath, interruptedState);
        await SetStatePhaseAsync(fixture.StatePath, "restoring");

        var result = await fixture.Service.RecoverInterruptedChangeAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.NotApplied, result.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task RecoverInterruptedRestore_ReturnsToAppliedWhenConfigWasNotWritten()
    {
        using var fixture = new ConfigFixture();
        await fixture.Service.ApplyAsync();
        var applied = await File.ReadAllTextAsync(fixture.ConfigPath);
        await SetStatePhaseAsync(fixture.StatePath, "restoring");

        var result = await fixture.Service.RecoverInterruptedChangeAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Applied, result.Status);
        Assert.Equal(applied, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.Equal("applied", await ReadStatePhaseAsync(fixture.StatePath));
    }

    [Fact]
    public async Task RecoverInterruptedChange_LeavesManagedConflictUntouched()
    {
        using var fixture = new ConfigFixture();
        await fixture.Service.ApplyAsync();
        await SetStatePhaseAsync(fixture.StatePath, "applying");
        var changed = (await File.ReadAllTextAsync(fixture.ConfigPath))
            .Replace("model = \"deepseek-v4-flash\"", "model = \"user-model\"");
        await File.WriteAllTextAsync(fixture.ConfigPath, changed);

        var result = await fixture.Service.RecoverInterruptedChangeAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Conflict, result.Status);
        Assert.Equal(changed, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.True(File.Exists(fixture.StatePath));
        Assert.Equal("applying", await ReadStatePhaseAsync(fixture.StatePath));
    }

    [Fact]
    public async Task Apply_WhenManagedFieldChangedAfterPriorApply_ReturnsConflict()
    {
        using var fixture = new ConfigFixture();
        await fixture.Service.ApplyAsync();
        var changed = (await File.ReadAllTextAsync(fixture.ConfigPath))
            .Replace("model = \"deepseek-v4-flash\"", "model = \"user-model\"");
        await File.WriteAllTextAsync(fixture.ConfigPath, changed);

        var result = await fixture.Service.ApplyAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Conflict, result.Status);
        Assert.Equal(changed, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    [Fact]
    public async Task Restore_FromEmptyConfig_RemovesSectionsCreatedByApply()
    {
        using var fixture = new ConfigFixture();
        await fixture.Service.ApplyAsync();

        var result = await fixture.Service.RestoreAsync();

        Assert.Equal(DeepSeekConfigChangeStatus.Restored, result.Status);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    private static async Task SetStatePhaseAsync(string statePath, string phase)
    {
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))
            ?? throw new InvalidDataException("State JSON was empty.");
        state["phase"] = phase;
        await File.WriteAllTextAsync(
            statePath,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);
    }

    private static async Task<string?> ReadStatePhaseAsync(string statePath)
    {
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath));
        return state?["phase"]?.GetValue<string>();
    }

    private sealed class ConfigFixture : IDisposable
    {
        public ConfigFixture(
            string modelCatalogRelativePath = @"codex\models.json",
            string helperRelativePath = @"bin\GptController.CredentialHelper.exe")
        {
            Root = Path.Combine(Path.GetTempPath(), "gpt-controller-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, "config.toml");
            ModelCatalogPath = Path.Combine(Root, modelCatalogRelativePath);
            StatePath = Path.Combine(Root, "deepseek-config-state.json");
            var helperPath = Path.Combine(Root, helperRelativePath);
            HelperPath = helperPath;
            Service = new DeepSeekCodexConfigService(
                new DeepSeekCodexConfigOptions(ConfigPath, ModelCatalogPath, StatePath, helperPath));
        }

        public string Root { get; }
        public string ConfigPath { get; }
        public string ModelCatalogPath { get; }
        public string StatePath { get; }
        public string HelperPath { get; }
        public DeepSeekCodexConfigService Service { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Test cleanup is best effort.
            }
        }
    }
}
