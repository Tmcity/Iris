@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-ui-v0210-evidence.ps1" -Stage offer
echo.
echo Keep the shown result and report any error.
pause
