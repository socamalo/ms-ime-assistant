@echo off
rem msimdict - CLI entry that works without a machine-wide .NET install
setlocal
if "%DOTNET_ROOT%"=="" set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "PATH=%DOTNET_ROOT%;%PATH%"

set "APP=%~dp0..\dist\cli\msimdict.dll"
if not exist "%APP%" (
  echo Cannot find %APP%
  echo Run first: pwsh -File build.ps1
  exit /b 1
)

"%DOTNET_ROOT%\dotnet.exe" "%APP%" %*
exit /b %ERRORLEVEL%
