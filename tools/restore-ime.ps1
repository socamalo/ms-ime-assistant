<#
  restore-ime.ps1 -- restore the IME data directory from a snapshot.

  Usage:
    pwsh -File tools/restore-ime.ps1 -List
    pwsh -File tools/restore-ime.ps1                       # latest snapshot
    pwsh -File tools/restore-ime.ps1 -Snapshot <dir>        # specific snapshot
    pwsh -File tools/restore-ime.ps1 -DryRun                # show what would change
    pwsh -File tools/restore-ime.ps1 -Pristine              # remove files we created

  -Pristine removes every file that is NOT in the snapshot (i.e. files this tool
  created, such as ChsPinyinEUDPv1.lex) and restores the snapshot's files.
#>
[CmdletBinding()]
param(
  [string] $Snapshot = '',
  [switch] $List,
  [switch] $DryRun,
  [switch] $Pristine,
  [string] $Root = ''
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $Root) { $Root = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..' }

$imeDir = Join-Path $env:APPDATA 'Microsoft\InputMethod\Chs'
$bakRoot = Join-Path (Resolve-Path $Root) '.local\ime-backups'

if ($List) {
  if (-not (Test-Path $bakRoot)) { Write-Host "no snapshots under $bakRoot"; exit 0 }
  Get-ChildItem $bakRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
    $mf = Join-Path $_.FullName 'manifest.json'
    if (Test-Path $mf) {
      $j = Get-Content $mf -Raw | ConvertFrom-Json
      "{0}  label={1,-22} files={2}  created={3}" -f $_.Name, $j.label, $j.files.Count, $j.createdAt
    } else { "{0}  (no manifest)" -f $_.Name }
  }
  exit 0
}

if (-not $Snapshot) {
  if (-not (Test-Path $bakRoot)) { throw "no snapshots under $bakRoot" }
  $snap = Get-ChildItem $bakRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
  if (-not $snap) { throw "no snapshots under $bakRoot" }
  $Snapshot = $snap.FullName
}
$Snapshot = (Resolve-Path $Snapshot).Path
$manifest = Get-Content (Join-Path $Snapshot 'manifest.json') -Raw | ConvertFrom-Json
$srcFiles = Join-Path $Snapshot 'files'

Write-Host "restoring from : $Snapshot"
Write-Host "  created      : $($manifest.createdAt)  label=$($manifest.label)"
Write-Host "  source dir   : $($manifest.sourceDir)"
Write-Host "  dry run      : $DryRun"
Write-Host ""

# --- what would change ---
$current = @{}
if (Test-Path $imeDir) {
  Get-ChildItem $imeDir -File | ForEach-Object { $current[$_.Name] = $_ }
}
$snapNames = @($manifest.files | ForEach-Object { $_.name })

$toRestore = @()
foreach ($f in $manifest.files) {
  $target = Join-Path $imeDir $f.name
  $have = $current.ContainsKey($f.name)
  $same = $false
  if ($have) {
    $same = ((Get-FileHash $target -Algorithm SHA256).Hash -eq $f.sha256)
  }
  if (-not $same) { $toRestore += $f }
}

$toRemove = @()
if ($Pristine) {
  foreach ($n in $current.Keys) {
    if ($snapNames -notcontains $n -and $current[$n].Extension -ne '.tmp') { $toRemove += $n }
  }
}

Write-Host "=== files to restore ==="
if ($toRestore.Count -eq 0) { Write-Host "  (none - already identical)" }
foreach ($f in $toRestore) {
  $state = if ($current.ContainsKey($f.name)) { 'differs' } else { 'missing' }
  "  {0,-34} {1}" -f $f.name, $state
}
if ($toRemove.Count -gt 0) {
  Write-Host "=== files to remove (pristine mode) ==="
  foreach ($n in $toRemove) { "  {0}" -f $n }
}

if ($DryRun) { Write-Host "`n(dry run, nothing written)"; exit 0 }

# --- do it ---
foreach ($f in $toRestore) {
  Copy-Item (Join-Path $srcFiles $f.name) (Join-Path $imeDir $f.name) -Force
}
foreach ($n in $toRemove) {
  Remove-Item (Join-Path $imeDir $n) -Force
}

# --- verify ---
$bad = @()
foreach ($f in $manifest.files) {
  $target = Join-Path $imeDir $f.name
  if (-not (Test-Path $target)) { $bad += "$($f.name) (missing)"; continue }
  if ((Get-FileHash $target -Algorithm SHA256).Hash -ne $f.sha256) { $bad += "$($f.name) (hash mismatch)" }
}
Write-Host ""
if ($bad.Count -eq 0) {
  Write-Host "restore complete, $($manifest.files.Count) files verified against manifest"
} else {
  Write-Host "restore finished with problems:"
  $bad | ForEach-Object { Write-Host "  $_" }
  exit 1
}
