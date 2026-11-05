@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-update-evidence.ps1" -Stage updated
if errorlevel 1 goto finished
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-v029-install-readonly.ps1"
:finished
echo.
echo Save the result and report any error shown above.
pause
