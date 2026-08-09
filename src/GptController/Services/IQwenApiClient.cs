using GptController.Models;

namespace GptController.Services;

public interface IQwenApiClient
{
    Task<IReadOnlyList<ApiModelDescriptor>> GetModelsAsync(
        string apiKey,
        QwenRegion region,
        string? workspaceId,
        CancellationToken cancellationToken = default);

    Task<ApiValidationResult> ValidateModelAsync(
        string apiKey,
        QwenRegion region,
        string? workspaceId,
        string model,
        CancellationToken cancellationToken = default);
}

public enum QwenApiErrorKind
{
    AuthenticationRequired,
    PaymentRequired,
    RateLimited,
    Timeout,
    Network,
    RemoteService,
    InvalidResponse,
    IncompatibleModel
}

public sealed class QwenApiException : Exception
{
    public QwenApiException(
        QwenApiErrorKind errorKind,
        string message,
        int? statusCode = null)
        : base(message)
    {
        ErrorKind = errorKind;
        StatusCode = statusCode;
    }

    public QwenApiErrorKind ErrorKind { get; }
    public int? StatusCode { get; }
}
