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
echo SendQueue with internal buffer 80 for comparison.
"build\win-x64\DualSenseBtAudio.exe" ^
  --device %AUDIO_INDEX% ^
  --full39 ^
  --headset ^
  --volume 100 ^
  --buffer 80 ^
  --period 21.333333
pause
