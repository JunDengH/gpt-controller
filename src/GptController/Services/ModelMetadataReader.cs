using System.Text.Json;
using GptController.Models;

namespace GptController.Services;

internal static class ModelMetadataReader
{
    public static ApiModelDescriptor DeepSeek(JsonElement item, string id) => new()
    {
        Id = id,
        DisplayName = Text(item, "name") ?? id,
        ContextWindowTokens = PositiveNumber(item, "context_window"),
        MaxOutputTokens = PositiveNumber(item, "max_output_tokens"),
        InputModalities = Strings(item, "input_modalities"),
        ReasoningEfforts = item.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object
            ? Strings(effort, "supported_levels") : [],
        DefaultReasoningEffort = item.TryGetProperty("effort", out var defaultEffort) && defaultEffort.ValueKind == JsonValueKind.Object
            ? Text(defaultEffort, "default_level") : null
    };

    public static ApiModelDescriptor Qwen(JsonElement item, string id) => new()
    {
        Id = id,
        DisplayName = Text(item, "name") ?? id,
        ContextWindowTokens = item.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object
            ? PositiveNumber(info, "context_window") : null,
        MaxOutputTokens = item.TryGetProperty("model_info", out var output) && output.ValueKind == JsonValueKind.Object
            ? PositiveNumber(output, "max_output_tokens") : null,
        InputModalities = item.TryGetProperty("inference_metadata", out var modalities) && modalities.ValueKind == JsonValueKind.Object
            ? Strings(modalities, "request_modality").Select(value => value.ToLowerInvariant()).ToArray() : [],
        Features = Strings(item, "features"),
        IsSnapshot = System.Text.RegularExpressions.Regex.IsMatch(id, @"-\d{4}-\d{2}-\d{2}(?:$|-)")
    };

    private static int? PositiveNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number > 0 ? number : null;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!).Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal).ToArray() : [];
}
