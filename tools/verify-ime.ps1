<#
  verify-ime.ps1 -- 端到端验收：写入 -> 切输入法 -> 打字提交 -> 回读文本。

  用法:
    pwsh -File tools/verify-ime.ps1 -Cli <msimdict.exe 路径> [-Word 龘靐齉]

  判定：打完编码按数字键提交，回读输入框文本；等于写入的词即命中。
  回读用「哨兵值 + 轮询」避免剪贴板延迟造成误判。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string] $Cli,
  [string] $Word = "龘靐齉",
  [int] $FullRank = 1,
  [int] $AbbrRank = 2
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class Im { 
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] p, int size);
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  public static void WinSpace() {
    const uint KEYUP = 0x0002;
    var i = new INPUT[4];
    i[0]=K(0,0x5B); i[1]=K(0,0x20); i[2]=K(KEYUP,0x20); i[3]=K(KEYUP,0x5B);
    SendInput(4, i, Marshal.SizeOf(typeof(INPUT)));
  }
  static INPUT K(uint flags, ushort vk){ var x=new INPUT(); x.type=1; x.u=new U();
    x.u.ki=new KEYBDINPUT{ wVk=vk, wScan=0, dwFlags=flags, time=0, dwExtraInfo=IntPtr.Zero}; return x; }
}
'@

Write-Host "=== 0. 清空自定义短语库 ==="
& $Cli clean | Out-Host
if ($LASTEXITCODE -ne 0) { throw "clean 失败" }

Write-Host ""
Write-Host "=== 1. 写入测试词条并部署 ==="
& $Cli add $Word | Out-Host
if ($LASTEXITCODE -ne 0) { throw "add 失败" }

Write-Host ""
Write-Host "=== 2. 落盘内容（应按编码升序）==="
& $Cli dump | Out-Host
& $Cli verify | Out-Host

$enc = & $Cli encode $Word | Out-String
$full = ([regex]::Match($enc, '全拼：(\S+)')).Groups[1].Value
$abbr = ([regex]::Match($enc, '简拼：(\S+)')).Groups[1].Value
if (-not $full -or -not $abbr) { throw "无法从 encode 输出里解析编码" }
Write-Host ""
Write-Host "全拼 = $full    简拼 = $abbr"

# --- 输入窗口 ---
$proc = Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) {
  Start-Process notepad | Out-Null
  Start-Sleep -Milliseconds 2500
  $proc = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}
$h = $proc.MainWindowHandle
$npid = $proc.Id
$SENTINEL = '__MSIMDICT_SENTINEL__'

function Focus-NP {
  for ($i = 0; $i -lt 12; $i++) {
    [Microsoft.VisualBasic.Interaction]::AppActivate($npid) | Out-Null
    [Im]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 250
    if ([Im]::GetForegroundWindow() -eq $h) { return $true }
  }
  return $false
}

function Read-Box {
  Set-Clipboard -Value $SENTINEL
  [System.Windows.Forms.SendKeys]::SendWait('^a')
  Start-Sleep -Milliseconds 150
  [System.Windows.Forms.SendKeys]::SendWait('^c')
  for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 100
    try {
      $v = Get-Clipboard -Raw
      if ($null -ne $v -and $v -ne $SENTINEL) { return ($v -replace "`r`n", '') }
    } catch { }
  }
  return '<剪贴板未更新>'
}

Write-Host ""
Write-Host "=== 3. 切换输入法，触发重新读取短语库 ==="
Focus-NP | Out-Null
Start-Sleep -Milliseconds 400
[Im]::WinSpace(); Start-Sleep -Milliseconds 1300
[Im]::WinSpace(); Start-Sleep -Milliseconds 1600
Write-Host "已切换"

function Test-Code($code, $digit, $expect, $label) {
  if (-not (Focus-NP)) { Write-Host "  [$label] 焦点失败"; return $false }
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait('^a')
  Start-Sleep -Milliseconds 200
  [System.Windows.Forms.SendKeys]::SendWait('{DEL}')
  Start-Sleep -Milliseconds 350
  [System.Windows.Forms.SendKeys]::SendWait($code)
  Start-Sleep -Milliseconds 1400
  [System.Windows.Forms.SendKeys]::SendWait($digit)
  Start-Sleep -Milliseconds 900
  $t = (Read-Box).Trim()
  $ok = ($t -eq $expect)
  Write-Host ("  [{0}] {1,-6} '{2}'+'{3}' => '{4}'   期望 '{5}'" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $label, $code, $digit, $t, $expect)
  return $ok
}

Write-Host ""
Write-Host "=== 4. 端到端验证 ==="
$r1 = Test-Code $full $FullRank $Word "全拼"
$r2 = Test-Code $abbr $AbbrRank $Word "简拼"

Write-Host ""
if ($r1 -and $r2) {
  Write-Host "结论：全拼与简拼都能精确命中，候选位与写入一致。"
  exit 0
} elseif ($r1 -or $r2) {
  Write-Host "结论：只有一种编码命中。"
  exit 2
} else {
  Write-Host "结论：两种编码都没命中，检查排序与切换是否生效。"
  exit 3
}
