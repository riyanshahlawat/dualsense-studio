@echo off
setlocal EnableExtensions
cd /d "%~dp0..\.."

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo ERROR: .NET 9 SDK was not found.
  echo Install the official Microsoft .NET 9 SDK and run this script again.
  echo.
  pause
  exit /b 1
)

echo Cleaning the previous publication...
if exist "build\win-x64" rmdir /s /q "build\win-x64"

echo.
echo Restoring packages...
dotnet restore src\DualSenseBtAudio\DualSenseBtAudio.csproj -r win-x64
if errorlevel 1 goto :error

echo.
echo Building the Release version...
dotnet publish src\DualSenseBtAudio\DualSenseBtAudio.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o build\win-x64
if errorlevel 1 goto :error

echo.
echo Locating the native HIDAPI library...
powershell.exe -NoProfile -ExecutionPolicy Bypass ^
  -File "%CD%\COPY_HIDAPI.ps1" ^
  -Destination "%CD%\build\win-x64" ^
  -RuntimeIdentifier "win-x64"
if errorlevel 1 goto :error

if not exist "build\win-x64\hidapi.dll" (
  echo.
  echo ERROR: hidapi.dll is not in the build\win-x64 folder.
  goto :error
)

if not exist "build\win-x64\DualSenseBtAudio.exe" (
  echo.
  echo ERROR: DualSenseBtAudio.exe was not created.
  goto :error
)

echo.
echo ============================================================
echo BUILD COMPLETE
echo Executable: %CD%\build\win-x64\DualSenseBtAudio.exe
echo HIDAPI:     %CD%\build\win-x64\hidapi.dll
echo ============================================================
echo.
pause
exit /b 0

:error
echo.
echo The build or HIDAPI copy failed.
echo Copy the complete message displayed above.
pause
exit /b 1
