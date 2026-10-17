@echo off
rem Installs the OUTSIDE reader + its own overlay into D:\Tools\tos-reader. Undo: uninstall-tos-reader.cmd
rem Nothing goes into the game folder. The in-game SoundSpy mod and the Other Side Sound overlay are not touched.
set "R=D:\Tools\tos-reader"
set "SRC=%~dp0TosReader"
set "OV=D:\Tools\tos-sound-overlay\app"

echo Building the reader...
dotnet build "%SRC%\TosReader.csproj" -c Release -o "%R%\bin" -v q -nologo || (echo Build FAILED & exit /b 1)

echo Copying the overlay (a separate copy, the original stays as it is)...
robocopy "%OV%" "%R%\overlay" /e /xf log.txt /njh /njs /nfl /ndl >nul
rem this copy reads the outside reader's feed instead of the in-game mod's
rem (plain text edit: the config has keys that differ only in case, which PowerShell's JSON reader refuses)
powershell -NoProfile -Command "$p='%R%\overlay\config.json'; $f='D:/Tools/tos-reader/soundfeed.txt'; $t=[IO.File]::ReadAllText($p); if ($t -match '\"GameFeedPath\"') { $t=$t -replace '\"GameFeedPath\":\s*\"[^\"]*\"', ('\"GameFeedPath\": \"'+$f+'\"') } else { $t=([regex]'\{').Replace($t, ('{'+[Environment]::NewLine+'  \"GameFeedPath\": \"'+$f+'\",'), 1) }; [IO.File]::WriteAllText($p, $t)"
findstr /c:"tos-reader/soundfeed.txt" "%R%\overlay\config.json" >nul || (echo Could not point the overlay copy at the reader feed & exit /b 1)

copy /y "%~dp0start-tos-reader.cmd" "%R%\" >nul
copy /y "%~dp0uninstall-tos-reader.cmd" "%R%\" >nul
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\Other Side Reader.lnk'); $s.TargetPath='%R%\start-tos-reader.cmd'; $s.WorkingDirectory='%R%'; $s.IconLocation='%R%\overlay\OtherSideSound.exe,0'; $s.WindowStyle=7; $s.Save()"

if exist "%R%\bin\TosReader.exe" (echo Installed. Desktop shortcut: "Other Side Reader") else (echo Install FAILED)
