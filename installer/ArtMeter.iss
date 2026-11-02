; ArtMeter installer (Inno Setup 6): per user, no admin rights, into %LOCALAPPDATA%\Programs\ArtMeter.
; Built by .github/workflows/build.yml after build.cmd:  ISCC /DAppVersion=0.2.0 installer\ArtMeter.iss  ->  dist\
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{BAE6EE2E-3EC7-451E-BDB1-9D0F575872A6}
AppName=ArtMeter
AppVersion={#AppVersion}
AppPublisher=artsocity23-ux
AppPublisherURL=https://github.com/artsocity23-ux/artmeter
AppSupportURL=https://github.com/artsocity23-ux/artmeter/issues
AppUpdatesURL=https://github.com/artsocity23-ux/artmeter/releases
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ArtMeter
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SourceDir=..
OutputDir=dist
OutputBaseFilename=ArtMeter-Setup-{#AppVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\ArtMeter.exe
UninstallDisplayName=ArtMeter
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; ArtMeter is closed by CloseArtMeter below, whichever copy is running
CloseApplications=no

[Languages]
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Files]
Source: "ArtMeter.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ArtMeter"; Filename: "{app}\ArtMeter.exe"

[Run]
; also runs on a silent install, so an update started with /VERYSILENT brings ArtMeter back
Filename: "{app}\ArtMeter.exe"; Description: "Запустити ArtMeter"; Flags: nowait postinstall

[UninstallDelete]
; written by ArtMeter itself next to the exe
Type: files; Name: "{app}\config.json"
Type: files; Name: "{app}\artmeter.log"
Type: files; Name: "{app}\artmeter.log.old"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(Access: Cardinal; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

// ArtMeter quits by itself when this event is set; a build too old to know the event is killed after 5 s.
procedure CloseArtMeter;
var
  Ev: THandle;
  I, Code: Integer;
begin
  if not CheckForMutexes('Local\ArtMeter.Single') then Exit;
  Ev := OpenEvent(EVENT_MODIFY_STATE, False, 'Local\ArtMeter.Quit');
  if Ev <> 0 then begin
    SetEvent(Ev);
    CloseHandle(Ev);
  end;
  for I := 1 to 50 do begin
    if not CheckForMutexes('Local\ArtMeter.Single') then Exit;
    Sleep(100);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ArtMeter.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseArtMeter;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CloseArtMeter;
  Result := True;
end;

// ArtMeter turns on autostart at its first run. Remove it, unless it points at another (portable) copy.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if (CurUninstallStep = usPostUninstall) and RegQueryStringValue(HKCU, RunKey, 'ArtMeter', Value) and
     (Pos(Lowercase(ExpandConstant('{app}\ArtMeter.exe')), Lowercase(Value)) > 0) then
    RegDeleteValue(HKCU, RunKey, 'ArtMeter');
end;
