@echo off
setlocal
set "SCRIPT_DIR=%~dp0"

if not exist "%SCRIPT_DIR%Config\APFPack.json" (
    echo ERROR: Config\APFPack.json is missing.
    echo Build a pack from a GameData fixture and copy the manifest first.
    exit /b 2
)
if not exist "%SCRIPT_DIR%Config\APFManifestUrl.txt" (
    echo ERROR: Config\APFManifestUrl.txt is missing.
    echo Put the HTTPS URL of the published APFPack.json in this file.
    exit /b 2
)

call "%SCRIPT_DIR%StartLunaServer.bat" %*
exit /b %ERRORLEVEL%
