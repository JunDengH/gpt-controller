namespace GptController.Infrastructure;

public static class LegacyCompatibility
{
    // These names deliberately remain on the 1.1.x identity so old and new
    // builds cannot mutate the shared ChatGPT/Codex state concurrently.
    public const string ApplicationMutexName =
        @"Local\GptAccountManager.Application";

    public const string SwitchMutexName =
        @"Local\GptAccountManager.Switch";

}
