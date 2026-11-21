@echo off
rem Builds the dedicated server into builds\Server. It doesn't need the game.
rem The output runs on Windows (SBMultiplayer.Server.exe) and anywhere with .NET 8
rem (dotnet SBMultiplayer.Server.dll), including the TrueNAS gameserver container:
rem copy the contents of builds\Server\SBMultiplayer.Server to /mnt/NAS/apps/sbmultiplayer/server.
setlocal
cd /d "%~dp0"

echo Building the server...
if exist builds\Server rmdir /s /q builds\Server
dotnet publish src\SBMultiplayer.Server -c Release -o builds\Server\SBMultiplayer.Server -nologo -v quiet
if errorlevel 1 goto fail
del /q builds\Server\SBMultiplayer.Server\*.pdb 2>nul
copy /y docs\hosting.md builds\Server\ >nul

echo.
echo Done: builds\Server\SBMultiplayer.Server
pause
exit /b 0

:fail
echo.
echo The server build failed.
pause
exit /b 1
