@echo off
setlocal
cd /d "%~dp0"

echo Checking v0.0.1 publications...
echo.

for %%R in (win-x64 win-x86) do (
  echo [%%R]
  if exist "build\%%R\DualSenseAudioStudio.exe" (echo [OK] Interface) else (echo [MISSING] Interface)
  if exist "build\%%R\DualSenseBtAudio.exe" (echo [OK] Engine) else (echo [MISSING] Engine)
  if exist "build\%%R\hidapi.dll" (echo [OK] HIDAPI) else (echo [MISSING] HIDAPI)
  echo.
)
pause
