using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GptController.Services;

namespace GptController.Tests;

public sealed class LocalProjectStateCompatibilityTests
{
    private static readonly string TargetKey = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes("[\"target-user\",\"target-account\"]")));

    [Fact]
    public void RecoversProjectsFromInactiveAccountWithoutMovingCloudState()
    {
        var root = CreateState();
        var beforeOwner = root[LocalProjectStateCompatibility.AccountStateKey]!["owner"]!.ToJsonString();
        var beforeSource = root[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]!["old"]!.DeepClone();
        var result = Apply(root);
        var target = TargetGlobals(result);

        Assert.NotNull(target["local-projects"]!["local-project"]);
        Assert.Null(target["local-projects"]!["g-p-cloud"]);
        Assert.Null(target["local-projects"]!["cloud-directory"]);
        Assert.Equal("local-project", target["thread-project-assignments"]!["local-thread"]!["projectId"]!.GetValue<string>());
        Assert.Null(target["thread-project-assignments"]!["cloud-thread"]);
        Assert.False(target["app-server-projects-migration-by-host"]!["local:home"]!["projectsMigrated"]!.GetValue<bool>());
        Assert.Equal("server-id", target["app-server-project-id-by-legacy-project-id-by-host"]!["local:home"]!["local-project"]!.GetValue<string>());
        Assert.Null(target["app-server-project-id-by-legacy-project-id-by-host"]!["ssh:other"]);
        Assert.Equal(beforeOwner, result[LocalProjectStateCompatibility.AccountStateKey]!["owner"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(beforeSource, result[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]!["old"]));
        Assert.Equal("target-cloud-state", result[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]![TargetKey]!["atoms"]!["chatgpt-sidebar-state-v2"]!.GetValue<string>());
        Assert.Null(target["remote-projects"]);
    }

    [Fact]
    public void NewAccountGetsOnlyLocalProjectionAndRepeatedPreparationIsNoOp()
    {
        var root = CreateState();
        root[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]!.AsObject().Remove(TargetKey);
        var result = Apply(root);
        Assert.Empty(result[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]![TargetKey]!["atoms"]!.AsObject());
        Assert.Null(LocalProjectStateCompatibility.Prepare(Encoding.UTF8.GetBytes(result.ToJsonString()), Credential()));
    }

    [Fact]
    public void TargetEditsWinAndAssignedThreadsAreNotMarkedProjectless()
    {
        var root = CreateState();
        var globals = TargetGlobals(root);
        globals["local-projects"] = JsonNode.Parse("""{"local-project":{"id":"local-project","name":"renamed","rootPaths":["D:\\code"]}}""");
        var target = TargetGlobals(Apply(root));
        Assert.Equal("renamed", target["local-projects"]!["local-project"]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(target["projectless-thread-ids"]!.AsArray(), n => n!.GetValue<string>() == "local-thread");
    }

    [Fact]
    public void SameAccountUpdatesActiveGlobalsRatherThanCreatingInactiveCopy()
    {
        var root = CreateState();
        root[LocalProjectStateCompatibility.AccountStateKey]!["owner"] = TargetKey;
        root[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]!.AsObject().Remove(TargetKey);
        var result = Apply(root);
        Assert.NotNull(result["local-projects"]!["local-project"]);
        Assert.Null(result[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]![TargetKey]);
    }

    [Fact]
    public void LegacyClientIsUntouchedAndUnknownSchemaFailsBeforeMutation()
    {
        Assert.Null(LocalProjectStateCompatibility.Prepare("{}"u8.ToArray(), Credential()));
        Assert.Throws<InvalidDataException>(() => LocalProjectStateCompatibility.Prepare(
            Encoding.UTF8.GetBytes("{\"electron-account-renderer-state-v1\":{\"inactive\":[]}}"), Credential()));
    }

    [Fact]
    public void LegacySavedFolderRestoresProjectAndMatchesHistoricalThreadHint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GptController.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = CreateState();
            var source = root[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]!["old"]!["globals"]!;
            source["electron-saved-workspace-roots"] = new JsonArray(directory);
            source["thread-workspace-root-hints"] = new JsonObject { ["legacy-thread"] = directory };
            var target = TargetGlobals(Apply(root));
            var id = target["thread-project-assignments"]!["legacy-thread"]!["projectId"]!.GetValue<string>();
            Assert.Equal(directory, target["local-projects"]![id]!["rootPaths"]![0]!.GetValue<string>());
            Assert.Equal(2, target["local-projects"]!.AsObject().Count);
        }
        finally { Directory.Delete(directory); }
    }

    private static JsonObject Apply(JsonObject state) => JsonNode.Parse(
        LocalProjectStateCompatibility.Prepare(Encoding.UTF8.GetBytes(state.ToJsonString()), Credential())!)!.AsObject();

    private static JsonObject TargetGlobals(JsonObject root) =>
        root[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]![TargetKey]!["globals"]!.AsObject();

    internal static byte[] Credential()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_user_id":"target-user","chatgpt_account_id":"target-account"}}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return JsonSerializer.SerializeToUtf8Bytes(new { tokens = new { account_id = "target-account", id_token = $"header.{payload}.signature", access_token = "access", refresh_token = "refresh" } });
    }

    internal static JsonObject CreateState() => JsonNode.Parse("""
        {
          "electron-account-renderer-state-v1": {
            "owner": "current",
            "inactive": {
              "old": {
                "atoms": {"chatgpt-sidebar-state-v2":"old-cloud-state"},
                "globals": {
                  "local-projects": {
                    "local-project":{"id":"local-project","name":"code","rootPaths":["D:\\code"]},
                    "g-p-cloud":{"id":"g-p-cloud","rootPaths":["D:\\cloud"]},
                    "cloud-directory":{"id":"cloud-directory","rootPaths":["C:\\Users\\test\\.codex\\.chatgpt-projects\\cloud"]}
                  },
                  "thread-project-assignments":{"local-thread":{"projectKind":"local","projectId":"local-project"},"cloud-thread":{"projectKind":"local","projectId":"g-p-cloud"}},
                  "projectless-thread-ids":["unassigned-thread","local-thread"],
                  "remote-projects":[{"id":"private-remote"}],
                  "app-server-project-id-by-legacy-project-id-by-host":{"local:home":{"local-project":"server-id","g-p-cloud":"cloud-server-id"},"ssh:other":{"local-project":"remote-server-id"}},
                  "app-server-projects-migration-by-host":{"local:home":{"projectsMigrated":true},"ssh:other":{"projectsMigrated":true}}
                }
              }
            }
          }
        }
        """)!.AsObject().WithTarget(TargetKey);
}

internal static class LocalStateTestExtensions
{
    internal static JsonObject WithTarget(this JsonObject root, string key)
    {
        root[LocalProjectStateCompatibility.AccountStateKey]!["inactive"]![key] = JsonNode.Parse("""{"atoms":{"chatgpt-sidebar-state-v2":"target-cloud-state"},"globals":{}}""");
        return root;
    }
}
