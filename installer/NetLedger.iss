; NetLedger installer. Built by scripts\publish.ps1, which passes AppVersion and PublishDir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#define ServiceName "NetLedger"

[Setup]
AppId={{6C1B6F3E-2B7A-4C8E-9C47-2E5B0E3D7A11}
AppName=NetLedger
AppVersion={#AppVersion}
AppVerName=NetLedger {#AppVersion}
AppPublisher=NetLedger contributors
DefaultDirName={autopf}\NetLedger
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=NetLedger-Setup-{#AppVersion}
SetupIconFile=..\src\NetLedger.Tray\Resources\NetLedger.ico
UninstallDisplayIcon={app}\NetLedger.exe
UninstallDisplayName=NetLedger
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
; The startup entry is per user, for the user running setup.
UsedUserAreasWarning=no

[Tasks]
Name: startup; Description: "Start NetLedger when I sign in"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NetLedger"; Filename: "{app}\NetLedger.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NetLedger"; \
  ValueData: """{app}\NetLedger.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; The stored image path must keep its own quotes ("\"C:\Program Files\...\""), or Windows may try C:\Program.exe first.
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\NetLedger.Service.exe\"""" start= auto DisplayName= ""NetLedger"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering the NetLedger service..."
Filename: "{sys}\sc.exe"; Parameters: "config {#ServiceName} binPath= ""\""{app}\NetLedger.Service.exe\"""" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Records network usage per hour, adapter and app for NetLedger."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\net.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting the NetLedger service..."
Filename: "{app}\NetLedger.exe"; Description: "Start NetLedger now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM NetLedger.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopTray"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\logman.exe"; Parameters: "stop NetLedger-KernelNetwork -ets"; Flags: runhidden waituntilterminated; RunOnceId: "StopTrace"

[Code]
const
  DesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  RuntimeDownload = 'https://dotnet.microsoft.com/download/dotnet/10.0';

function ListsVersion10(RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, DesktopRuntimeKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
        Result := True;
end;

function HasDesktopRuntime10: Boolean;
var
  FindRec: TFindRec;
begin
  // The runtime folder is what the app actually loads from, so it is the most reliable sign.
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
  begin
    FindClose(FindRec);
    Exit;
  end;

  // Otherwise ask the registry. .NET's own setup records versions in the 32-bit view, even for x64 runtimes.
  Result := ListsVersion10(HKLM32) or ListsVersion10(HKLM64);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDesktopRuntime10;
  if not Result then
  begin
    if MsgBox('NetLedger needs the .NET 10 Desktop Runtime (x64), which isn''t installed.' + #13#10#13#10 +
              'Open the download page now? Run this setup again once it is installed.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', RuntimeDownload, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Upgrades: release the files the running service and tray app hold open.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM NetLedger.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\NetLedger');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also delete your recorded usage history?' + #13#10#13#10 +
                'Choose No to keep it for a later reinstall (it stays in ' + DataDir + ').',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
