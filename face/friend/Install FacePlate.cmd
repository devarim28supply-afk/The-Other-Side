@echo off
rem Installs the in-game face plate. Quit The Other Side first.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install FacePlate.ps1"
echo.
pause
