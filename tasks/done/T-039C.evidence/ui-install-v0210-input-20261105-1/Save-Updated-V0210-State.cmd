@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-ui-v0210-evidence.ps1" -Stage updated
if errorlevel 1 goto finished
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-v0210-install-readonly.ps1"
:finished
echo.
echo Keep the shown result and report any error.
pause
