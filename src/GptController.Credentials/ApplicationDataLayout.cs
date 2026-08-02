namespace GptController.Credentials;

public static class ApplicationDataLayout
{
    public const string ApplicationDirectoryName = "GptController";
    public const string LegacyApplicationDirectoryName = "GptAccountManager";
    public const string DeepSeekProvider = "deepseek";
    public const string DeepSeekModel = "deepseek-v4-flash";
    public const string DeepSeekCredentialEntropyPurpose =
        "GptController/DeepSeekCredential/v2";
    public const string LegacyDeepSeekCredentialEntropyPurpose =
        "GptAccountManager/DeepSeekCredential/v1";

    public static string GetDefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new CredentialStoreException(
                "The local application data directory is unavailable.");
        }

        return Path.Combine(localAppData, ApplicationDirectoryName);
    }
}
