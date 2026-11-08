using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GptController.Services;

// Codex now scopes renderer globals by SHA256([userId, accountId]). Keep only
// filesystem project metadata shared when the same Windows user changes login.
// Never transplant renderer atoms, cloud projects, or authentication identities.
internal static class LocalProjectStateCompatibility
{
    internal const string AccountStateKey = "electron-account-renderer-state-v1";

    internal static byte[]? Prepare(byte[] original, ReadOnlySpan<byte> credential)
    {
        var root = JsonNode.Parse(original) as JsonObject
            ?? throw new InvalidDataException("Codex 本地项目状态格式无效。");
        if (root[AccountStateKey] is null) return null; // Older clients share state already.
        if (root[AccountStateKey] is not JsonObject accountState ||
            accountState["inactive"] is not JsonObject inactive)
            throw new InvalidDataException("Codex 账号项目状态格式已变化，已取消切换以保留原数据。");

        var targetKey = GetIdentityKey(credential);
        var scopes = new List<JsonObject> { root };
        foreach (var item in inactive)
        {
            if (item.Value is not JsonObject scope || scope["globals"] is not JsonObject globals)
                throw new InvalidDataException("Codex 保存的账号项目状态格式无效。");
            scopes.Add(globals);
        }

        var projects = new JsonObject();
        // An explicit active list (including an empty list) is authoritative.
        // Inactive caches are a bootstrap source only, never evidence to undo a
        // removal made in the active account.
        var hasActiveCatalog = root["local-projects"] is JsonObject;
        foreach (var scope in hasActiveCatalog ? scopes.Take(1) : scopes)
            if (scope["local-projects"] is JsonObject local)
                foreach (var project in local)
                    if (IsLocalProject(project.Key, project.Value) && !projects.ContainsKey(project.Key))
                        projects[project.Key] = project.Value!.DeepClone();
        // Saved folders and thread cwd hints can outlive project removal. They
        // must never be promoted into new project definitions.
        if (projects.Count == 0 && !hasActiveCatalog) return null;

        var before = root.DeepClone();
        var targetIsCurrent = accountState["owner"]?.GetValue<string>() == targetKey;
        JsonObject target;
        if (targetIsCurrent) target = root;
        else
        {
            if (inactive[targetKey] is null)
                inactive[targetKey] = new JsonObject { ["atoms"] = new JsonObject(), ["globals"] = new JsonObject() };
            target = inactive[targetKey]!["globals"]!.AsObject();
        }
        // Include existing target values first, so account-specific edits survive.
        var targetProjects = target["local-projects"] as JsonObject ?? new JsonObject();
        var removedIds = targetProjects.Where(p => IsLocalProject(p.Key, p.Value) && !projects.ContainsKey(p.Key))
            .Select(p => p.Key).ToHashSet();
        foreach (var id in removedIds) targetProjects.Remove(id);
        if (target["thread-project-assignments"] is JsonObject oldAssignments)
            foreach (var assignment in oldAssignments.ToList())
                if (assignment.Value is JsonObject value && value["projectKind"]?.GetValue<string>() == "local" &&
                    value["projectId"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && removedIds.Contains(id))
                    oldAssignments.Remove(assignment.Key);
        foreach (var key in new[] { "project-order", "pinned-project-ids" })
            if (target[key] is JsonArray ids)
                for (var index = ids.Count - 1; index >= 0; index--)
                    if (ids[index] is JsonValue value && value.TryGetValue<string>(out var id) && removedIds.Contains(id))
                        ids.RemoveAt(index);
        if (target["project-appearances"] is JsonObject appearances)
            foreach (var id in removedIds) appearances.Remove(id);
        foreach (var project in projects)
            if (!targetProjects.ContainsKey(project.Key)) targetProjects[project.Key] = project.Value!.DeepClone();
        target["local-projects"] = targetProjects;

        MergeMap(target, scopes, "thread-project-assignments", (_, v) =>
            v is JsonObject assignment && assignment["projectKind"]?.GetValue<string>() == "local" &&
            assignment["projectId"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && projects.ContainsKey(id));
        var assignments = target["thread-project-assignments"] as JsonObject ?? new JsonObject();
        foreach (var key in new[] { "thread-workspace-root-hints", "thread-projectless-output-directories" })
            MergeMap(target, scopes, key, (_, v) => v is JsonValue value &&
                value.TryGetValue<string>(out var path) && IsLocalPath(path));
        if (target["thread-workspace-root-hints"] is JsonObject hints)
            foreach (var hint in hints)
                if (!assignments.ContainsKey(hint.Key) && hint.Value is JsonValue value &&
                    value.TryGetValue<string>(out var path))
                {
                    var project = projects.FirstOrDefault(p => p.Value!["rootPaths"]!.AsArray().Any(r =>
                        string.Equals(NormalizePath(r!.GetValue<string>()), NormalizePath(path), StringComparison.OrdinalIgnoreCase)));
                    if (project.Key is not null)
                        assignments[hint.Key] = new JsonObject { ["projectKind"] = "local", ["projectId"] = project.Key };
                }
        if (assignments.Count > 0) target["thread-project-assignments"] = assignments;
        MergeMap(target, scopes, "thread-project-membership-host-ids", (id, v) =>
            assignments.ContainsKey(id) && v is JsonValue value && value.TryGetValue<string>(out var host) && host == "local");
        foreach (var key in new[] { "project-order", "pinned-project-ids" })
            MergeArray(target, scopes, key, projects.ContainsKey);
        MergeMap(target, scopes, "project-appearances", (id, _) => projects.ContainsKey(id));
        var serverIds = target["app-server-project-id-by-legacy-project-id-by-host"] as JsonObject ?? new JsonObject();
        foreach (var scope in scopes)
            if (scope["app-server-project-id-by-legacy-project-id-by-host"] is JsonObject hosts)
                foreach (var host in hosts.Where(h => h.Key.StartsWith("local:", StringComparison.Ordinal)).ToList())
                    if (host.Value is JsonObject ids)
                    {
                        var mapping = serverIds[host.Key] as JsonObject ?? new JsonObject();
                        foreach (var id in ids)
                            if (projects.ContainsKey(id.Key) && !mapping.ContainsKey(id.Key))
                                mapping[id.Key] = id.Value?.DeepClone();
                        if (mapping.Count > 0) serverIds[host.Key] = mapping;
                    }
        if (serverIds.Count > 0) target["app-server-project-id-by-legacy-project-id-by-host"] = serverIds;
        // These IDs are local Codex threads, not ChatGPT cloud conversations.
        MergeArray(target, scopes, "projectless-thread-ids", id =>
            !id.StartsWith("linked:", StringComparison.Ordinal) && !assignments.ContainsKey(id));

        // A new account can have an empty, already-completed migration. Invalidate
        // only local-host checkpoints so Codex imports the recovered associations.
        if (!JsonNode.DeepEquals(before, root))
        {
            var checkpoints = target["app-server-projects-migration-by-host"] as JsonObject ?? new JsonObject();
            foreach (var host in scopes.SelectMany(s =>
                         (s["app-server-projects-migration-by-host"] as JsonObject ?? new JsonObject()).Select(x => x.Key))
                     .Where(k => k.StartsWith("local:", StringComparison.Ordinal)).Distinct().ToList())
            {
                checkpoints[host] = new JsonObject
                {
                    ["version"] = 1, ["projectsMigrated"] = false,
                    ["threadAssignmentsMigrated"] = false, ["threadAssignmentsReadMigrated"] = false,
                    ["pendingThreadAssignmentIds"] = new JsonArray(assignments.Select(x => (JsonNode?)JsonValue.Create(x.Key)).ToArray())
                };
            }
            target["app-server-projects-migration-by-host"] = checkpoints;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        return null;
    }

    private static bool IsLocalProject(string id, JsonNode? node) =>
        !id.StartsWith("g-p-", StringComparison.Ordinal) &&
        !id.StartsWith("linked:", StringComparison.Ordinal) &&
        node is JsonObject project && project["rootPaths"] is JsonArray paths &&
        paths.Count > 0 && paths.All(p => p is JsonValue value &&
            value.TryGetValue<string>(out var path) && IsLocalPath(path));

    private static bool IsLocalPath(string path) => Path.IsPathFullyQualified(path) &&
        !path.Replace('/', '\\').Split('\\').Contains(".chatgpt-projects", StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static void MergeMap(JsonObject target, List<JsonObject> scopes, string key,
        Func<string, JsonNode?, bool> allow)
    {
        var result = target[key] as JsonObject ?? new JsonObject();
        foreach (var scope in scopes)
            if (scope[key] is JsonObject map)
                foreach (var entry in map)
                    if (!result.ContainsKey(entry.Key) && allow(entry.Key, entry.Value))
                        result[entry.Key] = entry.Value?.DeepClone();
        if (result.Count > 0) target[key] = result;
    }

    private static void MergeArray(JsonObject target, List<JsonObject> scopes, string key, Func<string, bool> allow)
    {
        var result = target[key] as JsonArray ?? new JsonArray();
        var seen = result.Select(v => v?.ToJsonString()).ToHashSet();
        foreach (var scope in scopes)
            if (scope[key] is JsonArray array)
                foreach (var value in array)
                    if (value is JsonValue node && node.TryGetValue<string>(out var text) &&
                        allow(text) && seen.Add(value.ToJsonString())) result.Add(value.DeepClone());
        if (result.Count > 0) target[key] = result;
    }

    private static string GetIdentityKey(ReadOnlySpan<byte> credential)
    {
        var auth = AuthDocument.Inspect(credential);
        foreach (var token in new[] { auth.IdToken, auth.AccessToken })
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            try
            {
                var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                if (!claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var identity)) continue;
                string? user = null;
                foreach (var field in new[] { "chatgpt_user_id", "user_id" })
                    if (identity.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    { user = value.GetString(); if (!string.IsNullOrEmpty(user)) break; }
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(auth.StoredAccountId)) continue;
                return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new[] { user, auth.StoredAccountId }))));
            }
            catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { }
        }
        throw new InvalidDataException("无法确认目标账号的本地项目状态标识，请重新添加该账号。");
    }
}
