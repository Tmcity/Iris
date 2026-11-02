@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-update-evidence.ps1" -Stage ready
echo.
echo Save the result and report any error shown above.
pause
