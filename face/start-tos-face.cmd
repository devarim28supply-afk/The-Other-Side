@echo off
rem Face cams for The Other Side. Nothing is written into the game folder.
cd /d "%~dp0"
start "TosFace server" /MIN node server.mjs
timeout /t 1 /nobreak >nul
start "" "%~dp0host\bin\TosFace.exe"
