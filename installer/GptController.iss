#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#define MyAppName "GPT Controller"
#define MyAppPublisher "GPT Controller contributors"
#define MyAppExeName "GptController.exe"
#define PublishDir "..\artifacts\publish"
#define LegacyAppName "GPT Account Manager"
#define LegacyAppExeName "GptAccountManager.exe"
#define LegacyCredentialHelperName "GptAccountManager.CredentialHelper.exe"
#define LegacyBareCredentialHelperName "CredentialHelper.exe"

[Setup]
AppId={{9D0E794D-FCB1-43BB-A8AA-7D831B9F5BC7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\GptController
UsePreviousAppDir=yes
UpdateUninstallLogAppName=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=GptController-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[InstallDelete]
; Remove only known, renamed application artifacts during an in-place 1.1.x upgrade.
; User data lives outside {app} and is intentionally not targeted here.
Type: files; Name: "{app}\{#LegacyAppExeName}"
Type: files; Name: "{app}\{#LegacyCredentialHelperName}"
Type: files; Name: "{app}\{#LegacyBareCredentialHelperName}"
Type: files; Name: "{autoprograms}\{#LegacyAppName}.lnk"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
