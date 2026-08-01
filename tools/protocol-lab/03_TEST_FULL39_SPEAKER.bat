@echo off
setlocal
cd /d "%~dp0..\.."
if not exist "build\win-x64\DualSenseBtAudio.exe" call tools\protocol-lab\BUILD_ENGINE_X64.bat
if errorlevel 1 exit /b 1
echo Main test: complete 0x39 protocol on the controller speaker.
"build\win-x64\DualSenseBtAudio.exe" --tone --full39 --speaker --volume 100 --seconds 5
pause
