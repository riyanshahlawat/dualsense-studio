@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Builds the app (x64) and then release\DualSenseStudio-Setup.exe with Inno Setup 6.
call "%~dp0BUILD_RUNTIME.bat" win-x64
if errorlevel 1 exit /b 1

set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup
  exit /b 1
)

"%ISCC%" /Q "%~dp0installer\DualSenseStudio.iss"
if errorlevel 1 exit /b 1

rem SHA-256 checksum next to the installer; the in-app updater refuses updates without it.
powershell -NoProfile -Command "$h=(Get-FileHash -Algorithm SHA256 '%~dp0release\DualSenseStudio-Setup.exe').Hash.ToLower(); Set-Content -NoNewline -Encoding ascii '%~dp0release\DualSenseStudio-Setup.exe.sha256' ($h + '  DualSenseStudio-Setup.exe')"
if errorlevel 1 exit /b 1
echo Installer: %CD%\release\DualSenseStudio-Setup.exe
echo Checksum:  %CD%\release\DualSenseStudio-Setup.exe.sha256
