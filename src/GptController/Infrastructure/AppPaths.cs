using GptController.Credentials;

namespace GptController.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(
        string? localAppData = null,
        string? userProfile = null,
        string? applicationDirectoryName = null)
    {
        var localRoot = localAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileRoot = userProfile
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directoryName = applicationDirectoryName
            ?? ApplicationDataLayout.ApplicationDirectoryName;

        if (string.IsNullOrWhiteSpace(directoryName) ||
            !string.Equals(
                Path.GetFileName(directoryName),
                directoryName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The application data directory name is invalid.",
                nameof(applicationDirectoryName));
        }

        LocalAppDataRoot = Path.GetFullPath(localRoot);
        UserProfileRoot = Path.GetFullPath(profileRoot);
        ApplicationDirectoryName = directoryName;
        Root = Path.Combine(LocalAppDataRoot, directoryName);
        Profiles = Path.Combine(Root, "profiles");
        Backups = Path.Combine(Root, "backups");
        Temp = Path.Combine(Root, "temp");
        Logs = Path.Combine(Root, "logs");
        Runtime = Path.Combine(Root, "runtime");
        CodexResources = Path.Combine(Root, "codex");
        Connections = Path.Combine(Root, "connections");
        IndexFile = Path.Combine(Root, "accounts.json");
        ConnectionIndexFile = Path.Combine(Connections, "index.json");
        SettingsFile = Path.Combine(Root, "settings.json");
        TransactionFile = Path.Combine(Root, "pending-switch.json");

        CodexHome = Path.Combine(UserProfileRoot, ".codex");
        LiveAuthFile = Path.Combine(CodexHome, "auth.json");
        CodexConfigFile = Path.Combine(CodexHome, "config.toml");
        DeepSeekModelCatalogFile = Path.Combine(CodexResources, "models.json");
        DeepSeekConfigStateFile = Path.Combine(CodexResources, "config-state.json");
    }

    public string LocalAppDataRoot { get; }
    public string UserProfileRoot { get; }
    public string ApplicationDirectoryName { get; }
    public string Root { get; }
    public string Profiles { get; }
    public string Backups { get; }
    public string Temp { get; }
    public string Logs { get; }
    public string Runtime { get; }
    public string CodexResources { get; }
    public string Connections { get; }
    public string IndexFile { get; }
    public string ConnectionIndexFile { get; }
    public string SettingsFile { get; }
    public string TransactionFile { get; }
    public string CodexHome { get; }
    public string LiveAuthFile { get; }
    public string CodexConfigFile { get; }
    public string DeepSeekModelCatalogFile { get; }
    public string DeepSeekConfigStateFile { get; }

    public string GetCredentialPath(Guid profileId) =>
        Path.Combine(Profiles, $"{profileId:N}.bin");

    public AppPaths CreateSibling(string applicationDirectoryName) =>
        new(LocalAppDataRoot, UserProfileRoot, applicationDirectoryName);

    public void EnsureCreated()
    {
        WindowsAcl.RestrictDirectoryToCurrentUser(Root);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(CodexResources);
        Directory.CreateDirectory(Connections);
    }
}
