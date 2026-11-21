@echo off
rem Builds the mod (the files players install) into builds\Mod.
rem   build-mod.bat                     uses the game folder saved in gamedir.txt, or asks for it once
rem   build-mod.bat "D:\...\STUNTBOOST"  uses (and saves) that game folder
rem The mod is compiled against the game's own DLLs, so it needs the STUNTBOOST install folder.
rem The build also copies the mod straight into the game's SBMultiplayer folder, ready to play.
setlocal
cd /d "%~dp0"

set "GAMEDIR=%~1"
if "%GAMEDIR%"=="" if exist gamedir.txt set /p GAMEDIR=<gamedir.txt
if "%GAMEDIR%"=="" set /p GAMEDIR=STUNTBOOST install folder (the one with STUNTBOOST.dll):
set "GAMEDIR=%GAMEDIR:"=%"
if not exist "%GAMEDIR%\STUNTBOOST.dll" (
    echo STUNTBOOST.dll isn't in "%GAMEDIR%". Check the folder and try again.
    if exist gamedir.txt del gamedir.txt
    goto fail
)
> gamedir.txt echo %GAMEDIR%

echo Building the mod against "%GAMEDIR%"...
dotnet build src\SBMultiplayer.Client -c Release -nologo -v quiet "-p:GameDir=%GAMEDIR%"
if errorlevel 1 goto fail

set "OUT=src\SBMultiplayer.Client\bin\Release\net8.0"
if exist builds\Mod rmdir /s /q builds\Mod
mkdir builds\Mod\SBMultiplayer
for %%F in (SBMultiplayer.Client.dll 0Harmony.dll LiteNetLib.dll SBMultiplayer.Protocol.dll SBMultiplayer.Server.dll) do (
    copy /y "%OUT%\%%F" builds\Mod\SBMultiplayer\ >nul || goto fail
)
copy /y packaging\INSTALL.txt builds\Mod\ >nul

echo.
echo Done: builds\Mod (copy the SBMultiplayer folder into the game folder, as INSTALL.txt says)
pause
exit /b 0

:fail
echo.
echo The mod build failed.
pause
exit /b 1
