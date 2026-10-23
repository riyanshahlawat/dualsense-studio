@echo off
set /p APPVER=<"%~dp0VERSION.txt"
setlocal EnableExtensions
cd /d "%~dp0"

if not exist "build\win-x64\DualSenseAudioStudio.exe" (
  echo ERROR: the x64 publication does not exist yet.
  exit /b 1
)

if not exist "build\win-x86\DualSenseAudioStudio.exe" (
  echo ERROR: the x86 publication does not exist yet.
  exit /b 1
)

if not exist "release" mkdir "release"

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "Compress-Archive -Path '%CD%\build\win-x64\*' -DestinationPath '%CD%\release\DualSense-Bluetooth-Audio_v%APPVER%_win-x64.zip' -Force"
if errorlevel 1 exit /b 1

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "Compress-Archive -Path '%CD%\build\win-x86\*' -DestinationPath '%CD%\release\DualSense-Bluetooth-Audio_v%APPVER%_win-x86.zip' -Force"
if errorlevel 1 exit /b 1

echo GitHub packages created in release\
exit /b 0
