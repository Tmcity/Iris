@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-update-evidence.ps1" -Stage offer
echo.
echo Save the result and report any error shown above.
pause
