<#
  build.ps1 -- 构建全部项目。

  用法:
    pwsh -File build.ps1              # Release
    pwsh -File build.ps1 -Configuration Debug
    pwsh -File build.ps1 -Test        # 构建后跑自检
#>
[CmdletBinding()]
param(
  [string] $Configuration = 'Release',
  [switch] $Test
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 找 dotnet：优先用户目录的免安装 SDK
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($cmd) { $dotnet = $cmd.Source } else { throw "找不到 dotnet SDK" }
}
Write-Host "dotnet: $dotnet"
& $dotnet --version

$projects = @(
  'src\MsImeDict.Core\MsImeDict.Core.csproj',
  'src\MsImeDict.Cli\MsImeDict.Cli.csproj',
  'tests\MsImeDict.Tests\MsImeDict.Tests.csproj',
  'src\MsImeDict.App\MsImeDict.App.csproj'
)

foreach ($p in $projects) {
  Write-Host ""
  Write-Host "=== building $p ==="
  & $dotnet build (Join-Path $root $p) -c $Configuration --nologo
  if ($LASTEXITCODE -ne 0) { throw "构建失败: $p" }
}

if ($Test) {
  Write-Host ""
  Write-Host "=== running self tests ==="
  $exe = Join-Path $root "tests\MsImeDict.Tests\bin\$Configuration\net8.0\msimdict-tests.exe"
  & $exe
  exit $LASTEXITCODE
}

Write-Host ""
Write-Host "构建完成"
