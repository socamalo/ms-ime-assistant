@echo off
rem msimdict-app - 托盘程序启动入口（免安装 .NET 环境也能跑）
setlocal
if "%DOTNET_ROOT%"=="" set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "PATH=%DOTNET_ROOT%;%PATH%"

set "APP=%~dp0..\dist\app\msimdict-app.exe"
if not exist "%APP%" (
  echo 找不到 %APP%
  echo 请先运行：pwsh -File build.ps1
  exit /b 1
)

start "" "%APP%"
