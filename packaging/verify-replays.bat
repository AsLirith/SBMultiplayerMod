@echo off
rem Re-simulates replays with the game to check that the runs are genuine (see docs/replay-verification.md).
rem Usage: drag a results folder exported by the server onto this file, or from a command prompt:
rem   verify-replays.bat <results folder>        every result -> verification.csv in that folder
rem   verify-replays.bat <file.rpl or folder>... replays -> verification.jsonl in the current folder
rem This file must stay in the SBMultiplayer folder of the game. Steam doesn't need to be running.
setlocal EnableDelayedExpansion
if "%~1"=="" (
  echo Usage: verify-replays.bat ^<results folder^> or ^<file.rpl or folder^>...
  pause
  exit /b 1
)
set "ARGS="
for %%A in (%*) do set ARGS=!ARGS! "%%~fA"
if "%SBMULTI_VERIFY%"=="" (
  if exist "%~f1\*_round*.json" (
    set "SBMULTI_VERIFY=%~f1\verification.jsonl"
  ) else (
    set "SBMULTI_VERIFY=%CD%\verification.jsonl"
  )
  if exist "!SBMULTI_VERIFY!" del "!SBMULTI_VERIFY!"
)
set "DOTNET_STARTUP_HOOKS=%~dp0SBMultiplayer.Client.dll"
pushd "%~dp0.."
echo Verifying replays, please wait (the game runs without a window)...
start "" /wait STUNTBOOST.exe !ARGS!
popd
echo Done. Report: !SBMULTI_VERIFY!
if exist "%~f1\verification.csv" type "%~f1\verification.csv"
pause
