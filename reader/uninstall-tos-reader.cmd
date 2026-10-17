@echo off
rem Removes the outside reader's desktop shortcut and stops it. Its folder D:\Tools\tos-reader stays (delete it by hand if wanted).
taskkill /im TosReader.exe /f >nul 2>&1
del "%USERPROFILE%\Desktop\Other Side Reader.lnk" >nul 2>&1
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do del "%%d\Other Side Reader.lnk" >nul 2>&1
echo Removed the "Other Side Reader" shortcut.
