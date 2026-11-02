@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0manual-stop-evidence.ps1"
echo.
echo Evidence stop requested. Preserve output before closing the Sandbox.
pause
