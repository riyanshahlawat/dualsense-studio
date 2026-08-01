@echo off
setlocal
cd /d "%~dp0..\.."

if not exist "build\win-x64\DualSenseBtAudio.exe" call tools\protocol-lab\BUILD_ENGINE_X64.bat
if errorlevel 1 exit /b 1

echo.
echo Available outputs:
"build\win-x64\DualSenseBtAudio.exe" --list-devices
echo.
set /p AUDIO_INDEX=Enter the number of the output where audio is playing: 

if "%AUDIO_INDEX%"=="" (
  echo No index was entered.
  pause
  exit /b 1
)

echo.
echo ClockFix: 1024 PCM to 960 Opus, 21.333333 ms cycle.
"build\win-x64\DualSenseBtAudio.exe" ^
  --device %AUDIO_INDEX% ^
  --full39 ^
  --speaker ^
  --volume 100 ^
  --period 21.333333
pause
