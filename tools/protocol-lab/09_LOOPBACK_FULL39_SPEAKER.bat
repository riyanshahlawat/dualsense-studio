@echo off
setlocal
cd /d "%~dp0..\.."
if not exist "build\win-x64\DualSenseBtAudio.exe" call tools\protocol-lab\BUILD_ENGINE_X64.bat
if errorlevel 1 exit /b 1
"build\win-x64\DualSenseBtAudio.exe" --full39 --speaker --volume 100
pause
