namespace GptAccountManager.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? localAppData = null, string? userProfile = null)
    {
        var localRoot = localAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileRoot = userProfile
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Root = Path.Combine(localRoot, "GptAccountManager");
        Profiles = Path.Combine(Root, "profiles");
        Backups = Path.Combine(Root, "backups");
        Temp = Path.Combine(Root, "temp");
        Logs = Path.Combine(Root, "logs");
        Runtime = Path.Combine(Root, "runtime");
        CodexResources = Path.Combine(Root, "codex");
        IndexFile = Path.Combine(Root, "accounts.json");
        SettingsFile = Path.Combine(Root, "settings.json");
        TransactionFile = Path.Combine(Root, "pending-switch.json");

        CodexHome = Path.Combine(profileRoot, ".codex");
        LiveAuthFile = Path.Combine(CodexHome, "auth.json");
        CodexConfigFile = Path.Combine(CodexHome, "config.toml");
        DeepSeekModelCatalogFile = Path.Combine(CodexResources, "models.json");
        DeepSeekConfigStateFile = Path.Combine(CodexResources, "config-state.json");
    }

    public string Root { get; }
    public string Profiles { get; }
    public string Backups { get; }
    public string Temp { get; }
    public string Logs { get; }
    public string Runtime { get; }
    public string CodexResources { get; }
    public string IndexFile { get; }
    public string SettingsFile { get; }
    public string TransactionFile { get; }
    public string CodexHome { get; }
    public string LiveAuthFile { get; }
    public string CodexConfigFile { get; }
    public string DeepSeekModelCatalogFile { get; }
    public string DeepSeekConfigStateFile { get; }

    public string GetCredentialPath(Guid profileId) =>
        Path.Combine(Profiles, $"{profileId:N}.bin");

    public void EnsureCreated()
    {
        WindowsAcl.RestrictDirectoryToCurrentUser(Root);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(CodexResources);
    }
}
