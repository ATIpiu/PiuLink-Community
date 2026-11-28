#define Original "PiuLink-Windows-" + SuiteVersion + "-win-x64.exe"
[Setup]
AppName=PiuLink Windows
AppVersion={#SuiteVersion}
AppPublisher=ATIPiu Labs
AppPublisherURL=https://github.com/ATIpiu/PiuLink-Community
OutputBaseFilename=PiuLink-Windows-{#SuiteVersion}-win-x64-compact
SetupIconFile=..\..\windows-host\src\PiuLink.Host\Assets\PiuLink.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
UsePreviousLanguage=no
ArchiveExtraction=enhanced/nopassword
WizardStyle=modern

[Files]
Source: "{#Archive}"; DestDir: "{tmp}"; DestName: "media.7z"; Flags: nocompression ignoreversion

[Run]
Filename: "{localappdata}\PiuLink\Installers\{#Original}"; Description: "Open PiuLink Windows Setup"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurStepChanged(Step: TSetupStep);
var
  Media, Bundle: String;
  Code: Integer;
begin
  if Step <> ssPostInstall then Exit;
  Media := ExpandConstant('{tmp}\media');
  Bundle := ExpandConstant('{localappdata}\PiuLink\Installers\{#Original}');
  WizardForm.StatusLabel.Caption := 'Unpacking PiuLink Windows...';
  ForceDirectories(Media);
  ExtractArchive(ExpandConstant('{tmp}\media.7z'), Media, '', True, nil);
  WizardForm.StatusLabel.Caption := 'Preparing the verified PiuLink Windows installer...';
  if not Exec(Media + '\RestoreBundle.exe', AddQuotes(Bundle), Media, SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    RaiseException('Could not restore and verify the installer. The full EXE is available on the download page.');
end;
