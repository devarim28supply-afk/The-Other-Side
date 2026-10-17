@echo off
rem Starts the outside reader (read-only, waits for the game; log in reader.log) and its overlay.
start "Other Side Reader" /min /D "%~dp0bin" "%~dp0bin\TosReader.exe"
start "" /D "%~dp0overlay" "%~dp0overlay\OtherSideSound.exe"
