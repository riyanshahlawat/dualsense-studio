@echo off
set /p APPVER=<"%~dp0VERSION.txt"
setlocal
cd /d "%~dp0"
call BUILD_RUNTIME.bat win-x86
if errorlevel 1 goto :error
echo.
echo v%APPVER% x86 built in build\win-x86
pause
exit /b 0

:error
echo.
echo The x86 build failed. Copy the message above.
pause
exit /b 1
