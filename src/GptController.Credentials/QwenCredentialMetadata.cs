using System.Text.Json.Serialization;

namespace GptController.Credentials;

public sealed record QwenCredentialMetadata
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = ApplicationDataLayout.QwenProvider;

    [JsonPropertyName("keyLastFour")]
    public required string KeyLastFour { get; init; }

    [JsonPropertyName("credentialFile")]
    public required string CredentialFile { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
