@echo off
setlocal
cd /d "%~dp0"

set "APP_PATH=build\win-x64\DualSenseAudioStudio.exe"
if not exist "%APP_PATH%" set "APP_PATH=build\win-x86\DualSenseAudioStudio.exe"

if not exist "%APP_PATH%" (
  echo The application has not been built yet.
  echo Run BUILD_APP.bat first.
  pause
  exit /b 1
)

start "" "%APP_PATH%"
