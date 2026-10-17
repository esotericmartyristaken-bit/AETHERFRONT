@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "LOCAL_DOTNET_ROOT=%SCRIPT_DIR%..\.dotnet6"
if exist "%LOCAL_DOTNET_ROOT%\dotnet.exe" (
    set "DOTNET_ROOT=%LOCAL_DOTNET_ROOT%"
    set "DOTNET_ROOT_X64=%LOCAL_DOTNET_ROOT%"
)
"%SCRIPT_DIR%MasterServer.exe" /apfonly %*
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" pause
exit /b %EXIT_CODE%
