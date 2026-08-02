namespace GptAccountManager.Models;

public sealed record DeepSeekResponseTestResult
{
    public required string ResponseId { get; init; }
    public required string OutputText { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
}
