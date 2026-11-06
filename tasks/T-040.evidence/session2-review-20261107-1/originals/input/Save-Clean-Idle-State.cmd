@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0t040_save_clean_idle_readonly.ps1"
echo ExitCode=%ERRORLEVEL%
pause
