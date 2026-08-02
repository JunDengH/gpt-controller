namespace GptAccountManager.Services;

public static class CredentialHelperLocator
{
    public const string ExecutableName = "GptAccountManager.CredentialHelper.exe";

    public static string Locate(string? applicationDirectory = null)
    {
        var directory = applicationDirectory ?? AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(directory, ExecutableName));
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "DeepSeek 凭据助手缺失，请重新安装完整的应用包。",
            candidate);
    }
}
