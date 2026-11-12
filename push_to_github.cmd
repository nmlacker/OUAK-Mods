@echo off
rem Double-click to push the OUAK mods source to your private GitHub repository.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0push_to_github.ps1" %*
echo.
pause
