@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0capture-manual-view.ps1" -Stage updated-v0210
echo.
pause
