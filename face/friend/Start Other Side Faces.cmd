@echo off
rem Face cams for The Other Side. Nothing is installed into the game.
rem Start the game, then run this. Close the small "Other Side faces" window to quit.
start "Other Side Faces reader" /min /D "%~dp0reader" "%~dp0reader\TosReader.exe"
start "" /D "%~dp0face" "%~dp0face\TosFace.exe"
rem The sound overlay draws over the game. It reads soundfeed.txt from the reader above.
start "" /D "%~dp0overlay" "%~dp0overlay\OtherSideSound.exe"
