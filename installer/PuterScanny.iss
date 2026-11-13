; PuterScanny installer (Inno Setup 6). Built by build\release.ps1, which publishes the app first.
; Manual build:  ISCC.exe /DAppVersion=1.0.0 installer\PuterScanny.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "PuterScanny"
#define AppExe "PuterScanny.exe"
#define PublishDir "..\artifacts\publish"

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{275E5F8E-0C59-4F90-AB10-72AF5ADFF05A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=pedromussi1
AppPublisherURL=https://github.com/pedromussi1/PuterScanny
AppSupportURL=https://github.com/pedromussi1/PuterScanny/issues
AppUpdatesURL=https://github.com/pedromussi1/PuterScanny/releases
AppCopyright=Copyright (c) 2026 pedromussi1. MIT licence.
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

; Installs for the current user without administrator rights by default; "all users" is offered.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 or newer (the app's minimum).
MinVersion=10.0.19041

LicenseFile=..\LICENSE
SetupIconFile=..\src\PuterScanny.App\Assets\PuterScanny.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Close a running PuterScanny before updating or uninstalling it.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An upgrade replaces the program files completely, so nothing from an older version lingers.
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "See what's on your drive and what's safe to delete"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// On uninstall, offer to remove PuterScanny's own data (saved scans, settings, downloaded AI models).
// Files the user put in quarantine, and the record needed to restore them, are never removed by the uninstaller.

function DataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\PuterScanny');
end;

procedure DeleteIfExists(const Path: String);
begin
  if FileExists(Path) then DeleteFile(Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir, Note: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := DataDir();
  if not DirExists(Dir) then Exit;

  Note := '';
  if DirExists(Dir + '\Quarantine') or FileExists(Dir + '\cleanup-history.json') then
    Note := #13#10#13#10 + 'Files you put in quarantine are kept either way (in ' + Dir + '\Quarantine, ' +
            'or a "PuterScanny Quarantine" folder at the top of a drive). Reinstall PuterScanny to restore them, or delete them yourself.';

  if SuppressibleMsgBox('Also remove PuterScanny''s saved scans, settings and downloaded AI models?' + #13#10#13#10 +
       'They are in ' + Dir + '. The AI model alone can be several GB.' + Note,
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(Dir + '\Scans', True, True, True);
    DelTree(Dir + '\Models', True, True, True);
    DeleteIfExists(Dir + '\ai-settings.json');
    DeleteIfExists(Dir + '\ai-explanations.db');
    DeleteIfExists(Dir + '\ai-explanations.db-wal');
    DeleteIfExists(Dir + '\ai-explanations.db-shm');
    RemoveDir(Dir); // only succeeds when nothing (such as quarantine) is left
  end;
end;
