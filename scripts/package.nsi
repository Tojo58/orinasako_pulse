Unicode true
!cd ..
!include "LogicLib.nsh"
!ifdef NSIS_WIN32_MAKENSIS
  !define BUILDSEP '\'
!else
  !define BUILDSEP '/'
!endif
!ifndef APP_VERSION
  !error "Pass -DAPP_VERSION from Directory.Build.props"
!endif
Name "Orinasako Pulse"
OutFile "OrinasakoPulse-Setup-${APP_VERSION}-win-x64.exe"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
Page instfiles
Section "Install or update Orinasako Pulse"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\payload"
  File /r /x "*.pdb" "artifacts${BUILDSEP}package${BUILDSEP}payload${BUILDSEP}*.*"
  SetOutPath "$PLUGINSDIR\installer"
  File /r /x "*.pdb" "artifacts${BUILDSEP}package${BUILDSEP}installer${BUILDSEP}*.*"
  ExecWait '"$PLUGINSDIR\installer\Orinasako.Pulse.Installer.exe" ensure --source "$PLUGINSDIR\payload" --minimum-version ${APP_VERSION}' $0
  ${If} $0 != 0
    SetErrorLevel 1
    Abort "Orinasako Pulse could not be installed or started."
  ${EndIf}
  SetErrorLevel 0
SectionEnd
