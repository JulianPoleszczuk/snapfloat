; SnapFloat installer (Inno Setup 6). Built by build.ps1, see README.
; Per-user install: no administrator rights, installs to %LOCALAPPDATA%\Programs\SnapFloat.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define AppGuid "6C1F7D3A-5B2E-4C8A-9E41-2F9B7A0D5C13"

[Setup]
AppId={{{#AppGuid}}
AppName=SnapFloat
AppVersion={#AppVersion}
AppVerName=SnapFloat {#AppVersion}
AppPublisher=Julian Poleszczuk
DefaultDirName={autopf}\SnapFloat
DefaultGroupName=SnapFloat
DisableProgramGroupPage=yes
; Per-user only. An elevated SnapFloat could not drag files into normal (non-elevated) apps.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=SnapFloat-Setup-{#AppVersion}
SetupIconFile=..\src\SnapFloat\Assets\SnapFloat.ico
UninstallDisplayIcon={app}\SnapFloat.exe
UninstallDisplayName=SnapFloat
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=SnapFloat

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Offered on the first install only. On an upgrade the app's own setting (Settings > start with Windows) is kept:
; re-applying the remembered task would switch startup back on for users who turned it off in the app.
Name: "startup"; Description: "Start SnapFloat when I sign in to Windows (recommended)"; GroupDescription: "Startup:"; Check: not IsUpgrade
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SnapFloat"; Filename: "{app}\SnapFloat.exe"; Comment: "Floating screenshot previews you can drag anywhere"
Name: "{autodesktop}\SnapFloat"; Filename: "{app}\SnapFloat.exe"; Tasks: desktopicon

[Registry]
; Same value the app writes from Settings, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SnapFloat"; ValueData: """{app}\SnapFloat.exe"" --background"; Tasks: startup
; Always remove the startup entry on uninstall, even if it was enabled later from Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SnapFloat"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\SnapFloat.exe"; Description: "Launch SnapFloat"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\SnapFloat.exe"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "StopSnapFloat"

[Code]
// True when SnapFloat is already installed for this user (Inno Setup's own uninstall entry exists).
function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKA, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1');
end;

// Stop a running copy before files are replaced (upgrade / reinstall).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\SnapFloat.exe');
  if FileExists(Exe) then
    Exec(Exe, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Optionally remove settings and logs. Screenshots are never removed: neither the user's folder (Pictures\Screenshots
// by default) nor DataDir\Screenshots, the backup folder used when the chosen folder can't be written.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\SnapFloat');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also remove SnapFloat''s settings and logs in' + #13#10 + DataDir + '?' + #13#10#13#10 +
                'Your screenshots are kept.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DeleteFile(DataDir + '\settings.json');
        DeleteFile(DataDir + '\settings.json.tmp');
        DeleteFile(DataDir + '\settings.json.corrupt');
        DelTree(DataDir + '\logs', True, True, True);
        RemoveDir(DataDir); // only succeeds when nothing (such as backup screenshots) is left
      end;
  end;
end;
