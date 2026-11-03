@echo off
:wait_for_release
if exist "%~dp0release.request" exit /b 0
ping -n 2 127.0.0.1 >nul
goto wait_for_release
