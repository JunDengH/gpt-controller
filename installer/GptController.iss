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
AppMutex=Local\GptAccountManager.Application

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

[Code]
const
  RestoreConfigArgument = '--restore-codex-config-for-uninstall';
  RestoreExitSwitchInProgress = 20;
  RestoreExitConflict = 21;
  RestoreExitUnsafeConfiguration = 22;
  RestoreExitFailure = 23;

function RestoreFailureMessage(ResultCode: Integer): String;
begin
  case ResultCode of
    RestoreExitSwitchInProgress:
      Result := '另一个账号或 API Provider 切换仍在进行，无法安全恢复 Codex 配置。';
    RestoreExitConflict:
      Result := 'Codex config.toml 的受管字段在启用 Provider 后发生了外部修改，自动恢复可能覆盖用户配置。';
    RestoreExitUnsafeConfiguration:
      Result := 'Codex config.toml 仍引用 GPT Controller 管理的 DeepSeek/Qwen Provider，但缺少可安全使用的恢复状态。';
    RestoreExitFailure:
      Result := '恢复 Codex 配置失败。加密备份可能缺失、无法解密，或配置文件不可访问。';
  else
    Result := Format('恢复 Codex 配置失败（退出码 %d）。', [ResultCode]);
  end;
end;

procedure AbortUninstallForRestoreFailure(const FailureMessage: String);
var
  FullMessage: String;
begin
  FullMessage := FailureMessage + #13#10 + #13#10 +
    '为避免删除凭据助手后破坏 Codex，卸载已取消，程序文件会被保留。' + #13#10 +
    '请先在 GPT Controller 中切换回 ChatGPT，处理配置冲突后再重试卸载。';
  Log(FullMessage);
  if not UninstallSilent then
    MsgBox(FullMessage, mbCriticalError, MB_OK);
  Abort;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ApplicationPath: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  ApplicationPath := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ApplicationPath) then
    AbortUninstallForRestoreFailure(
      '找不到 GPT Controller 的配置恢复程序，无法验证 Codex 配置是否可安全卸载。');

  if not Exec(
      ApplicationPath,
      RestoreConfigArgument,
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
    AbortUninstallForRestoreFailure(
      '无法启动 GPT Controller 的配置恢复程序：' + SysErrorMessage(ResultCode));

  if ResultCode <> 0 then
    AbortUninstallForRestoreFailure(RestoreFailureMessage(ResultCode));

  Log('Codex managed Provider configuration is safe to uninstall.');
end;
