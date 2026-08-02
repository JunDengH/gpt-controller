using GptAccountManager.Models;

namespace GptAccountManager.Services;

public interface IDeepSeekApiClient
{
    Task<DeepSeekBalanceSnapshot> GetBalanceAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<DeepSeekResponseTestResult> TestResponseAsync(
        string apiKey,
        CancellationToken cancellationToken = default);
}

public enum DeepSeekApiErrorKind
{
    AuthenticationRequired,
    PaymentRequired,
    RateLimited,
    Timeout,
    Network,
    RemoteService,
    InvalidResponse
}

public sealed class DeepSeekApiException : Exception
{
    public DeepSeekApiException(
        DeepSeekApiErrorKind errorKind,
        string message,
        int? statusCode = null)
        : base(message)
    {
        ErrorKind = errorKind;
        StatusCode = statusCode;
    }

    public DeepSeekApiErrorKind ErrorKind { get; }
    public int? StatusCode { get; }
}
