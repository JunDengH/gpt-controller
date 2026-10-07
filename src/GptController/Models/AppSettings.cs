namespace GptController.Models;

public sealed record AppSettings
{
    public const int DefaultRefreshMinutes = 2;
    public int RefreshPolicyVersion { get; init; } = 1;
    public int QuotaRefreshMinutes { get; init; } = DefaultRefreshMinutes;
    public bool CloseToTray { get; init; } = true;
    public bool StartMinimized { get; init; }
}
