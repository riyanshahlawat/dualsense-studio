@echo off
rem DualSense Studio setup: asks for administrator approval once, then runs Setup.ps1.
powershell.exe -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0Setup.ps1'"
