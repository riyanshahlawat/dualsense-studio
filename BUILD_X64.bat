@echo off
set /p APPVER=<"%~dp0VERSION.txt"
setlocal
cd /d "%~dp0"
call BUILD_RUNTIME.bat win-x64
if errorlevel 1 goto :error
echo.
echo v%APPVER% x64 built in build\win-x64
pause
exit /b 0

:error
echo.
echo The x64 build failed. Copy the message above.
pause
exit /b 1
