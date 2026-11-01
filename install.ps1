# Bazaar Placement AI 一键安装脚本（Windows PowerShell 5.1+）
#
# 安装：  irm https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/install.ps1 | iex
# 指定游戏目录或卸载：
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/install.ps1))) -GameDir "D:\SteamLibrary\steamapps\common\The Bazaar"
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/install.ps1))) -Uninstall
#
# 做的事：找到游戏目录 → 检查已装好 LookingIN → 下载本插件 → 从 LookingIN 官方仓库下载两个 SDK DLL 并校验
# SHA256 → 备份旧版本后安装。不会改动 LookingIN 或游戏本身的任何文件。
param([string]$GameDir = $env:BAZAAR_GAME_DIR, [switch]$Uninstall)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Version = '0.1.7'
$PackageUrl = "https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/release/BazaarPlacementAI-$Version.zip"
$LookingInPage = 'https://github.com/LookingIN97/LookingIN-Distribution/releases/latest'
$VerifiedLookingIn = @('0.247.6', '0.248.0')  # 实测验证过的 LookingIN 版本
# LookingIN 官方公开 SDK（MIT），固定到经过测试的版本，并校验哈希
$SdkBase = 'https://github.com/LookingIN97/LookingIN-Distribution/raw/v0.248.0/sdk'
$Sdk = [ordered]@{
  'LookingIN.Compute.Client.dll'    = '00B3697D2732DC9C1F4BAA42BF639143FB95FEA00AFC8D959E6360B126F8F13F'
  'LookingIN.Compute.Contracts.dll' = '3AE15445E7F59BA5A8F683E21233CD688ED9F2CE1430235437A53082001AAF00'
}

function Say($msg, $color = 'Gray') { Write-Host $msg -ForegroundColor $color }
function Fail($msg) { throw $msg }

function Find-GameDir {
  $roots = New-Object System.Collections.Generic.List[string]
  foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
    try {
      $p = Get-ItemProperty $key -ErrorAction Stop
      foreach ($v in $p.SteamPath, $p.InstallPath) { if ($v) { $roots.Add(($v -replace '/', '\')) } }
    } catch { }
  }
  $libs = New-Object System.Collections.Generic.List[string]
  foreach ($r in $roots) {
    $libs.Add($r)
    $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
      foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
    }
  }
  foreach ($l in ($libs | Select-Object -Unique)) {
    $g = Join-Path $l 'steamapps\common\The Bazaar'
    if (Test-Path (Join-Path $g 'TheBazaar.exe')) { return $g }
  }
  return $null
}

function Get-LookingInVersion($dll) {
  # BepInPlugin 声明里写着 "LookingIN" 加版本号
  $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
  $m = [regex]::Match($text, 'LookingIN[\x01-\x1f](\d+\.\d+\.\d+)')
  if ($m.Success) { return $m.Groups[1].Value }
  return $null
}

Say "`n=== Bazaar Placement AI $Version 安装 ===`n" 'Cyan'

try {
# 1. 游戏目录
if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir) {
  Say '没有自动找到游戏目录。'
  $GameDir = Read-Host '请输入 The Bazaar 的游戏目录（TheBazaar.exe 所在的文件夹）'
}
$GameDir = $GameDir.Trim('"', ' ')
if (-not (Test-Path (Join-Path $GameDir 'TheBazaar.exe'))) { Fail "这个目录里没有 TheBazaar.exe：$GameDir" }
Say "游戏目录：$GameDir" 'Green'

$Plugins = Join-Path $GameDir 'BepInEx\plugins'
$Target = Join-Path $Plugins 'BazaarPlacementAI'

# 2. 游戏必须关闭
if (Get-Process TheBazaar -ErrorAction SilentlyContinue) { Fail '游戏正在运行，请先关闭 The Bazaar 再运行本脚本。' }

# 卸载
if ($Uninstall) {
  if (-not (Test-Path $Target)) { Say '没有找到已安装的 Bazaar Placement AI，无需卸载。' 'Yellow'; return }
  Remove-Item $Target -Recurse -Force
  Say "已删除 $Target。LookingIN 不受影响。" 'Green'
  return
}

# 3. 检查 LookingIN
$LookingInDll = Join-Path $Plugins 'LookingIN\LookingIN.dll'
if (-not (Test-Path $LookingInDll)) {
  Fail "没有找到 LookingIN 插件（$LookingInDll）。请先按 LookingIN 的说明安装最新版并进游戏确认能用：$LookingInPage"
}
$found = Get-LookingInVersion $LookingInDll
if ($VerifiedLookingIn -contains $found) {
  Say "已找到 LookingIN $found" 'Green'
} else {
  $shown = if ($found) { $found } else { '未知' }
  Say "检测到的 LookingIN 版本是 $shown，本插件实测验证过的是 $($VerifiedLookingIn -join '、')。其他版本通常也能用，出问题时会自动退回 LookingIN 原搜索。" 'Yellow'
  $ans = Read-Host '仍然继续安装吗？(y/N)'
  if ($ans -notmatch '^[yY]') { Say "已取消。LookingIN 官方发布页：$LookingInPage"; return }
}

# 4. 下载
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("BazaarPlacementAI-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  Say '正在下载 Bazaar Placement AI…'
  $zip = Join-Path $tmp 'package.zip'
  Invoke-WebRequest -Uri $PackageUrl -OutFile $zip -UseBasicParsing
  Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp 'pkg') -Force
  $src = Join-Path $tmp 'pkg\BepInEx\plugins\BazaarPlacementAI'
  if (-not (Test-Path (Join-Path $src 'BazaarPlacementAI.dll'))) { Fail '下载的安装包不完整，请稍后重试。' }

  Say '正在从 LookingIN 官方仓库下载 SDK…'
  foreach ($name in $Sdk.Keys) {
    $out = Join-Path $src $name
    Invoke-WebRequest -Uri "$SdkBase/$name" -OutFile $out -UseBasicParsing
    $hash = (Get-FileHash $out -Algorithm SHA256).Hash
    if ($hash -ne $Sdk[$name]) { Fail "$name 校验失败（SHA256 不一致），已停止安装，没有改动游戏目录。" }
  }
  Say 'SDK 校验通过' 'Green'

  # 5. 备份旧版本后安装。备份必须放在 plugins 外面，否则 BepInEx 会把旧版也加载进来
  if (Test-Path $Target) {
    $backupRoot = Join-Path $GameDir 'BepInEx\BazaarPlacementAI-backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backup = Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item $Target $backup
    Say "旧版本已备份到 $backup"
  }
  Copy-Item $src $Target -Recurse
} finally {
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Say "`n安装完成：$Target" 'Green'
Say '启动游戏后按 G 智能搜索；Alt+G 强制原算法；Ctrl+G 强制 AI。'
Say '卸载：在命令后加 -Uninstall 再运行一次，或直接删除上面这个文件夹。'
} catch {
  Write-Host "`n[失败] $($_.Exception.Message)" -ForegroundColor Red
}
