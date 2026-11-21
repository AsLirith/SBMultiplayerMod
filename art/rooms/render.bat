@echo off
rem Renders the pictures of the Multiplayer page (800x1250) with headless Edge, then saves them as JPEG (the paper grit
rem makes PNGs ~1.4 MB each) into the client project, which embeds them in SBMultiplayer.Client.dll.
rem Run it after editing one of the .html files here, then rebuild the mod.
setlocal
cd /d "%~dp0"
set "EDGE=C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
set "OUT=%~dp0..\..\src\SBMultiplayer.Client\Rooms\Art"
if not exist "%OUT%" mkdir "%OUT%"
set "TMPDIR=%TEMP%\sbm-room-art"
if not exist "%TMPDIR%" mkdir "%TMPDIR%"
for %%N in (public sblb tournaments host settings) do (
    if exist "%TMPDIR%\%%N.png" del "%TMPDIR%\%%N.png"
    "%EDGE%" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --allow-file-access-from-files --window-size=800,1250 "--screenshot=%TMPDIR%\%%N.png" "file:///%~dp0%%N.html" >nul 2>&1
    if exist "%TMPDIR%\%%N.png" (
        powershell -NoProfile -Command "Add-Type -AssemblyName System.Drawing; $img=[Drawing.Image]::FromFile('%TMPDIR%\%%N.png'); $enc=[Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | ? MimeType -eq 'image/jpeg'; $p=New-Object Drawing.Imaging.EncoderParameters 1; $p.Param[0]=New-Object Drawing.Imaging.EncoderParameter([Drawing.Imaging.Encoder]::Quality, [long]88); $img.Save('%OUT%\%%N.jpg', $enc, $p); $img.Dispose()"
        echo %%N.jpg
    ) else (echo %%N FAILED)
)
