@echo off
setlocal
cd /d "%~dp0..\.."

if not exist "build\win-x64\DualSenseBtAudio.exe" call tools\protocol-lab\BUILD_ENGINE_X64.bat
if errorlevel 1 exit /b 1

echo.
echo First, review the output list:
echo.
"build\win-x64\DualSenseBtAudio.exe" --list-devices
echo.
set /p AUDIO_INDEX=Enter the number of the output playing your browser or game: 

if "%AUDIO_INDEX%"=="" (
  echo No index was entered.
  pause
  exit /b 1
)

echo.
echo Capturing output [%AUDIO_INDEX%] and sending it to the headset jack...
"build\win-x64\DualSenseBtAudio.exe" --device %AUDIO_INDEX% --full39 --headset --volume 100
pause
