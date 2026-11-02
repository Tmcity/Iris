@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0manual-start-evidence.ps1"
if errorlevel 1 goto finished
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0manual-capture-state.ps1" -Stage clean
:finished
echo.
pause
