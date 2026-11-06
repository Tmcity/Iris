@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0t040_capture_feature_view_manual.ps1"
echo ExitCode=%ERRORLEVEL%
pause
