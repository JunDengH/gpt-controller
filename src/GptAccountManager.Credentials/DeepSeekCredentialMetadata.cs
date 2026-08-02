using System.Text.Json.Serialization;

namespace GptAccountManager.Credentials;

public sealed record DeepSeekCredentialMetadata
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = ApplicationDataLayout.DeepSeekProvider;

    [JsonPropertyName("model")]
    public string Model { get; init; } = ApplicationDataLayout.DeepSeekModel;

    [JsonPropertyName("keyLastFour")]
    public required string KeyLastFour { get; init; }

    [JsonPropertyName("credentialFile")]
    public required string CredentialFile { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
