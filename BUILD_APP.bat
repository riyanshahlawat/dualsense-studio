@echo off
set /p APPVER=<"%~dp0VERSION.txt"
setlocal EnableExtensions
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo ERROR: .NET 9 SDK was not found.
  echo Install the official Microsoft .NET 9 SDK and run this script again.
  echo.
  pause
  exit /b 1
)

echo Building both v%APPVER% architectures...

call BUILD_RUNTIME.bat win-x64
if errorlevel 1 goto :error

call BUILD_RUNTIME.bat win-x86
if errorlevel 1 goto :error

call PACKAGE_RELEASE.bat
if errorlevel 1 goto :error

echo.
echo ============================================================
echo DUALSENSE BLUETOOTH AUDIO v%APPVER% BUILT
echo.
echo x64: %CD%\build\win-x64\DualSenseAudioStudio.exe
echo x86: %CD%\build\win-x86\DualSenseAudioStudio.exe
echo GitHub ZIPs: %CD%\release
echo ============================================================
echo.
pause
exit /b 0

:error
echo.
echo The build failed. Copy the message displayed above.
pause
exit /b 1
