<#
  backup-ime.ps1 -- snapshot Microsoft Pinyin user data before touching it.

  Usage:
    pwsh -File tools/backup-ime.ps1
    pwsh -File tools/backup-ime.ps1 -Label before-first-write

  Writes a timestamped snapshot under .local/ime-backups/<stamp>/ with:
    files/   copy of every non-temp file in the IME data directory
    manifest.json  sha256 + size + mtime for each file, plus provenance notes
#>
[CmdletBinding()]
param(
  [string] $Label = '',
  [string] $Root = ''
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $Root) { $Root = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..' }

$imeDir = Join-Path $env:APPDATA 'Microsoft\InputMethod\Chs'
if (-not (Test-Path $imeDir)) { throw "IME data dir not found: $imeDir" }

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$destRoot = Join-Path (Resolve-Path $Root) '.local\ime-backups'
$dest = Join-Path $destRoot $stamp
$filesDir = Join-Path $dest 'files'
New-Item -ItemType Directory -Force -Path $filesDir | Out-Null

Write-Host "IME data dir : $imeDir"
Write-Host "snapshot     : $dest"

$manifest = [ordered]@{
  stamp        = $stamp
  label        = $Label
  createdAt    = (Get-Date).ToString('o')
  machine      = $env:COMPUTERNAME
  user         = $env:USERNAME
  sourceDir    = $imeDir
  files        = @()
  notes        = @(
    'ChsPinyinUDL.dat / ChsPinyinIH.dat are maintained by ChsIME.exe itself.',
    'This tool never writes them; they are captured for restore-on-corruption only.',
    'Files absent from this snapshot did not exist when it was taken.'
  )
}

$copied = 0
foreach ($f in Get-ChildItem $imeDir -File | Sort-Object Name) {
  if ($f.Extension -eq '.tmp') { continue }   # 481 zero-byte leftovers, no value
  Copy-Item $f.FullName (Join-Path $filesDir $f.Name) -Force
  $manifest.files += [ordered]@{
    name    = $f.Name
    size    = $f.Length
    mtime   = $f.LastWriteTime.ToString('o')
    sha256  = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
  }
  $copied++
}

# record the zero-byte .tmp residue so a restore knows to leave it alone
$tmpCount = (Get-ChildItem $imeDir -File -Filter '*.tmp' -ErrorAction SilentlyContinue).Count
$manifest.tmpResidueCount = $tmpCount

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dest 'manifest.json') -Encoding UTF8

Write-Host "copied $copied files (.tmp residue skipped: $tmpCount)"
Write-Host ""
Write-Host "=== snapshot contents ==="
Get-ChildItem $filesDir -File | ForEach-Object {
  $m = $manifest.files | Where-Object { $_.name -eq $_.Name } | Select-Object -First 1
  "{0,-34} {1,8} B" -f $_.Name, $_.Length
}
Write-Host ""
Write-Host "manifest: $(Join-Path $dest 'manifest.json')"
Write-Host "restore : pwsh -File tools/restore-ime.ps1 -Snapshot $dest"
