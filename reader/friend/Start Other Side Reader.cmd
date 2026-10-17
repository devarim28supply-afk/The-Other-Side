@echo off
rem Other Side Reader: shows The Other Side's sounds on screen. Reads the game from OUTSIDE, read-only.
rem Start the game first or after, either way works. F8 = hide/show overlay, Ctrl+F8 = quit overlay.
start "Other Side Reader" /min /D "%~dp0reader" "%~dp0reader\TosReader.exe"
start "" /D "%~dp0overlay" "%~dp0overlay\OtherSideSound.exe"
