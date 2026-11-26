@echo off
rem msimdict - 命令行入口（免安装 .NET 环境也能跑）
setlocal
if "%DOTNET_ROOT%"=="" set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "PATH=%DOTNET_ROOT%;%PATH%"

set "APP=%~dp0..\dist\cli\msimdict.dll"
if not exist "%APP%" (
  echo 找不到 %APP%
  echo 请先运行：pwsh -File build.ps1
  exit /b 1
)

"%DOTNET_ROOT%\dotnet.exe" "%APP%" %*
exit /b %ERRORLEVEL%
