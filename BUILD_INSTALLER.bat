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
echo Installer: %CD%\release\DualSenseStudio-Setup.exe
