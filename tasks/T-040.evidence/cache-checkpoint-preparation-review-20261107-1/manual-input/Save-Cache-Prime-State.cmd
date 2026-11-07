@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0t040_save_cache_checkpoint_readonly.ps1" -Phase prime
set "T040_CACHE_EVIDENCE_EXIT=%ERRORLEVEL%"
echo.
echo Evidence exit code: %T040_CACHE_EVIDENCE_EXIT%
pause
exit /b %T040_CACHE_EVIDENCE_EXIT%
