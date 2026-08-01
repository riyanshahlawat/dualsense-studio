@echo off
setlocal EnableExtensions
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 9 SDK was not found.
  exit /b 1
)

set "RUNTIME=%~1"
if /i "%RUNTIME%"=="win-x64" goto :runtime_ok
if /i "%RUNTIME%"=="win-x86" goto :runtime_ok

echo ERROR: invalid architecture. Use win-x64 or win-x86.
exit /b 1

:runtime_ok
set "OUTPUT_DIR=%CD%\build\%RUNTIME%"

echo.
echo ============================================================
echo Publishing DualSense Bluetooth Audio v0.0.1 for %RUNTIME%
echo ============================================================

if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"

dotnet restore "src\DualSenseAudioStudio\DualSenseAudioStudio.csproj" -r "%RUNTIME%"
if errorlevel 1 exit /b 1

dotnet restore "src\DualSenseBtAudio\DualSenseBtAudio.csproj" -r "%RUNTIME%"
if errorlevel 1 exit /b 1

dotnet publish "src\DualSenseAudioStudio\DualSenseAudioStudio.csproj" ^
  -c Release ^
  -r "%RUNTIME%" ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o "%OUTPUT_DIR%"
if errorlevel 1 exit /b 1

dotnet publish "src\DualSenseBtAudio\DualSenseBtAudio.csproj" ^
  -c Release ^
  -r "%RUNTIME%" ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o "%OUTPUT_DIR%"
if errorlevel 1 exit /b 1

powershell.exe -NoProfile -ExecutionPolicy Bypass ^
  -File "%~dp0COPY_HIDAPI.ps1" ^
  -Destination "%OUTPUT_DIR%" ^
  -RuntimeIdentifier "%RUNTIME%"
if errorlevel 1 exit /b 1

if not exist "%OUTPUT_DIR%\DualSenseAudioStudio.exe" exit /b 1
if not exist "%OUTPUT_DIR%\DualSenseBtAudio.exe" exit /b 1
if not exist "%OUTPUT_DIR%\hidapi.dll" exit /b 1

copy /y "README.md" "%OUTPUT_DIR%\README.md" >nul
copy /y "VERSION.txt" "%OUTPUT_DIR%\VERSION.txt" >nul
copy /y "THIRD_PARTY_NOTICES.md" "%OUTPUT_DIR%\THIRD_PARTY_NOTICES.md" >nul

echo %RUNTIME% publication complete: %OUTPUT_DIR%
exit /b 0
