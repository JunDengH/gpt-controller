namespace GptAccountManager.Models;

public sealed record AppSettings
{
    public int QuotaRefreshMinutes { get; init; } = 15;
    public bool CloseToTray { get; init; } = true;
    public bool StartMinimized { get; init; }
}
