@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0t040_save_feature_state_readonly.ps1"
echo ExitCode=%ERRORLEVEL%
pause
