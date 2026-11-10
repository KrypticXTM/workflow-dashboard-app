; Workflow Dashboard - per-user Inno Setup installer (no admin prompt).
; Build (from repo root, after publishing):
;   dotnet publish WorkflowDashboard\WorkflowDashboard.csproj -c Release -r win-x64 --self-contained false -o publish\win-x64
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\WorkflowDashboard.iss
; Overrides: /DPublishDir=<folder> /DMyAppVersion=1.2.3 /O<outputdir>
; Silent install (used by the future auto-updater): WorkflowDashboard-Setup-x.y.z.exe /SILENT /SUPPRESSMSGBOXES /NORESTART

#define MyAppName "Workflow Dashboard"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#define MyAppPublisher "Tim Murray"
#define MyAppURL "https://github.com/KrypticXTM/workflow-dashboard"
#define MyAppExeName "WorkflowDashboard.exe"
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
; Keep AppId stable across versions so upgrades replace the existing install.
AppId={{0727313D-47A1-4090-8491-BA73D74AAF4F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\Workflow Dashboard
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=WorkflowDashboard-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; Close a running dashboard (Restart Manager) before replacing files.
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
; TODO: SetupIconFile=..\WorkflowDashboard\Assets\app.ico  (no app icon exists yet)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; TODO(updates): for silent auto-updates, add a second entry without skipifsilent (or pass a /RELAUNCH flag) to restart the app.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Graceful close (WM_CLOSE, no /F) so the dashboard can restore linked windows before files are removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "CloseWorkflowDashboard"

[Code]
// Framework-dependent build: needs the .NET 8 Desktop Runtime (x64).
function IsDesktopRuntime8Installed(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDesktopRuntime8Installed() then
    Result := SuppressibleMsgBox('Workflow Dashboard needs the .NET 8 Desktop Runtime (x64), which was not found.' + #13#10#13#10 +
      'Download ".NET Desktop Runtime 8.x (x64)" from https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10#13#10 +
      'Continue installing anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;
