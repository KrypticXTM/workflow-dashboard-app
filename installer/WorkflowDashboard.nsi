; Workflow Dashboard - per-user NSIS installer (no admin prompt).
; Build (from repo root, after publishing):
;   dotnet publish WorkflowDashboard/WorkflowDashboard.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64
;   makensis installer/WorkflowDashboard.nsi
; Output: installer/Output/WorkflowDashboard-Setup-<ver>.exe
; Unsigned: Windows SmartScreen may warn — More info > Run anyway.
Unicode True
RequestExecutionLevel user
SetCompressor /SOLID lzma

!define APPNAME "Workflow Dashboard"
!ifndef APPVER
  !define APPVER "1.2.3"
!endif
!ifndef PAYLOAD
  !define PAYLOAD "..\publish\win-x64"
!endif

Name "${APPNAME} ${APPVER}"
OutFile "Output\WorkflowDashboard-Setup-${APPVER}.exe"
InstallDir "$LOCALAPPDATA\Programs\WorkflowDashboard"
InstallDirRegKey HKCU "Software\WorkflowDashboard" "InstallDir"
BrandingText "${APPNAME} ${APPVER}"

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Section "Install"
  SetOutPath "$INSTDIR"
  ; Close a running copy gracefully (no /F) so files can be replaced.
  nsExec::ExecToLog 'taskkill /IM WorkflowDashboard.exe'
  File /r "${PAYLOAD}\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\WorkflowDashboard" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "DisplayVersion" "${APPVER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "Publisher" "KrypticXTM (Tim Murray)"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "DisplayIcon" "$INSTDIR\WorkflowDashboard.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard" "NoRepair" 1

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\WorkflowDashboard.exe" "" "$INSTDIR\WorkflowDashboard.exe" 0
  CreateShortCut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog 'taskkill /IM WorkflowDashboard.exe'
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkflowDashboard"
  DeleteRegKey HKCU "Software\WorkflowDashboard"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"
SectionEnd
