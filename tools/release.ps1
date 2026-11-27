<#
  release.ps1 -- 出一条正式 Release。

  做这些事，任何一步失败就停：
    1. 从 Directory.Build.props 读版本号（全仓库唯一来源）
    2. 本地 tag 已存在就报错（同一个版本号只能发一次）
    3. 跑自检，不全绿不往下走
    4. 发布自包含的 win-x64 应用包
    5. 打 tag 并推送
    6. 用 CHANGELOG.md 里对应那一节做 Release 说明，上传资产

  用法：
    powershell -File tools\release.ps1 -DryRun     # 只打包，不推不打 tag
    powershell -File tools\release.ps1             # 正式发
#>
[CmdletBinding()]
param(
  [switch] $DryRun,
  [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
Set-Location $root

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if (-not $cmd) { throw "找不到 dotnet SDK" }
  $dotnet = $cmd.Source
}

# ---------- 1. 版本号 ----------

$props = [IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'), [Text.Encoding]::UTF8)
$m = [regex]::Match($props, '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>')
if (-not $m.Success) { throw "Directory.Build.props 里找不到 <Version>" }
$version = $m.Groups[1].Value
$tag = "v$version"

Write-Host "版本 $version（tag $tag）"

# ---------- 2. tag 不能重复 ----------

$existing = git tag -l $tag
if ($existing) { throw "tag $tag 已存在：一个版本号只能发一次，改版本号后再来" }

# ---------- 3. 自检 ----------

if (-not $SkipTests) {
  Write-Host ""
  Write-Host "=== 自检 ==="
  & $dotnet run --project tests\MsImeDict.Tests\MsImeDict.Tests.csproj -c Release --nologo
  if ($LASTEXITCODE -ne 0) { throw "自检未通过，不发布" }
}

# ---------- 4. 打包 ----------

$stage = Join-Path $root 'dist\release\msimdict-app-win-x64'
$zip   = Join-Path $root "dist\release\msimdict-app-win-x64.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip)   { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host ""
Write-Host "=== 发布自包含包 ==="
& $dotnet publish src\MsImeDict.App\MsImeDict.App.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=false -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

foreach ($f in @('README.md', 'CHANGELOG.md', 'LICENSE')) {
  Copy-Item (Join-Path $root $f) (Join-Path $stage $f) -Force
}

# 出厂体检：包里不能出现真名、版本号要对
$packedReadme = [IO.File]::ReadAllText((Join-Path $stage 'README.md'), [Text.Encoding]::UTF8)
if ($packedReadme -match '丁燚') { throw "包内 README 出现了真名，停下来" }
if ($packedReadme -notmatch [regex]::Escape($version)) { throw "包内 README 的版本号不是 $version" }

$exe = Join-Path $stage 'msimdict-app.exe'
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($exeVersion -ne $version) { throw "可执行文件版本是 $exeVersion，不是 $version" }

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "包：$zip（$sizeMb MB，exe 版本 $exeVersion）"

# ---------- 5. Release 说明 ----------

$changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'), [Text.Encoding]::UTF8)
$lines = $changelog -split "`r?`n"
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match "^## \[$([regex]::Escape($version))\]") { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md 里没有 $version 那一节" }

$body = New-Object System.Collections.Generic.List[string]
for ($i = $start; $i -lt $lines.Count; $i++) {
  if ($i -gt $start -and $lines[$i] -match '^## \[') { break }
  $body.Add($lines[$i])
}
$notes = ($body -join "`n").TrimEnd() -replace '-{3,}\s*$', ''
$notes = $notes.TrimEnd()

$notesFile = Join-Path $root 'dist\release\notes.md'
[IO.File]::WriteAllText($notesFile, $notes + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "说明：$notesFile（$($notes.Length) 字符）"

if ($DryRun) {
  Write-Host ""
  Write-Host "DryRun 结束：包和说明都准备好了，没有打 tag、没有推远端"
  exit 0
}

# ---------- 6. tag 与 Release ----------

Write-Host ""
Write-Host "=== 打 tag 并推送 ==="
git tag -a $tag -m $tag
if ($LASTEXITCODE -ne 0) { throw "打 tag 失败" }
git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "推送 tag 失败" }

Write-Host ""
Write-Host "=== 创建 Release ==="
gh release create $tag $zip --title "$tag" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "创建 Release 失败（tag 已推，重跑上传即可）" }

Write-Host ""
Write-Host "完成：https://github.com/socamalo/ms-ime-assistant/releases/tag/$tag"
