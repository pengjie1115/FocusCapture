# fetch-lark-cli.ps1 —— 把 lark-cli 拉进 runtime\lark-cli\
#
# 用途：Skill（如 feishu-kb-manager）要调 lark-cli 才能读写飞书，但不能要求用户先装 Node、
#       更不能依赖「本机恰好装过某个别的应用」。所以把官方二进制拉进应用目录，随应用分发。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\fetch-lark-cli.ps1
#   powershell -ExecutionPolicy Bypass -File tools\fetch-lark-cli.ps1 -Version 1.0.96 -Force
#
# 下载顺序（2026-09-20 实测）：npmmirror 的二进制镜像 → GitHub releases 兜底。
#   镜像路径规则抄自官方包自己的安装脚本（@larksuite/cli/scripts/install.js）：
#     https://registry.npmmirror.com/-/binary/lark-cli/v<版本>/lark-cli-<版本>-windows-amd64.zip
#   走镜像的理由很实在：GitHub 在本机网络上不稳（SSL/502），47MB 从 GitHub 拉大概率失败。
#
# 为什么要放进应用目录（而不是像 Python 那样只当个可选件）：
#   ① 定位确定 —— 应用自己知道自己那份 CLI 在哪，不去猜 PATH；
#   ② 凭据独立 —— lark-cli 按「工作空间」分目录存配置（无宿主标记时用 ~/.lark-cli\ 根目录，
#      带 HERMES_HOME 时用 ~/.lark-cli\hermes\），所以应用内的授权与别的宿主**互不干扰**。

param(
    [string]$Version = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
$destRoot = Join-Path $repoRoot "runtime\lark-cli"
$exeName  = "lark-cli.exe"

Write-Host "目标目录：$destRoot"

if ((Test-Path (Join-Path $destRoot $exeName)) -and -not $Force) {
    Write-Host "[skip] 已存在 $exeName，未做任何改动（要重拉请加 -Force）"
    exit 0
}

# ── 版本：没指定就问 registry（取 latest，避免把版本号写死在脚本里失真）──
if ([string]::IsNullOrWhiteSpace($Version)) {
    try {
        $meta = Invoke-RestMethod -Uri "https://registry.npmmirror.com/@larksuite/cli/latest" -TimeoutSec 60
        $Version = $meta.version
        Write-Host "[ver ] 从 registry 解析到 latest = $Version"
    }
    catch {
        Write-Host "无法解析最新版本（$($_.Exception.Message)）。请显式传 -Version。"
        exit 1
    }
}

$archive = "lark-cli-$Version-windows-amd64.zip"
$urls = @(
    "https://registry.npmmirror.com/-/binary/lark-cli/v$Version/$archive",
    "https://github.com/larksuite/cli/releases/download/v$Version/$archive"
)

$zipPath = Join-Path $env:TEMP $archive
$downloaded = $false
foreach ($u in $urls) {
    try {
        Write-Host "[get ] $u"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-WebRequest -Uri $u -OutFile $zipPath -UseBasicParsing -TimeoutSec 600
        $sw.Stop()
        $mb = (Get-Item $zipPath).Length / 1MB
        Write-Host ("[ ok ] {0:N1} MB / {1:N1}s" -f $mb, ($sw.ElapsedMilliseconds / 1000))
        $downloaded = $true
        break
    }
    catch {
        Write-Host "[fail] $($_.Exception.Message)"
    }
}
if (-not $downloaded) {
    Write-Host "所有下载源均失败。请检查网络后重试。"
    exit 1
}

# ── 删旧目录前先自保：路径必须落在本仓库的 runtime\lark-cli 下 ──
if (-not $destRoot.EndsWith("runtime\lark-cli")) {
    Write-Host "内部错误：目标路径不符合预期（$destRoot），已中止以免误删。"
    exit 2
}
if (Test-Path $destRoot) {
    Remove-Item -LiteralPath $destRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $destRoot -Force | Out-Null

$expandDir = Join-Path $env:TEMP ("lark-cli-unzip-" + (Get-Random))
if (Test-Path $expandDir) { Remove-Item -LiteralPath $expandDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $expandDir -Force

$found = Get-ChildItem -LiteralPath $expandDir -Recurse -File -Filter $exeName | Select-Object -First 1
if ($null -eq $found) {
    Write-Host "压缩包里没找到 $exeName —— 官方包结构可能变了，请人工确认。"
    Get-ChildItem -LiteralPath $expandDir -Recurse -File | ForEach-Object { Write-Host "  $($_.FullName)" }
    exit 3
}
Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $destRoot $exeName) -Force
Remove-Item -LiteralPath $expandDir -Recurse -Force -ErrorAction SilentlyContinue

# ── 自检：必须真的能跑（"文件在" ≠ "能用"）──
$exePath = Join-Path $destRoot $exeName
$out = & $exePath --version 2>&1 | Out-String
Write-Host "[self] $($out.Trim())"

if ($out -notmatch "lark-cli version") {
    Write-Host ""
    Write-Host "自检未通过：$exeName 跑不起来（上面是它的输出）。"
    Write-Host "这会让依赖它的 Skill 全部不可用，请先解决再继续。"
    exit 3
}

$sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host ""
Write-Host "完成。lark-cli $Version（$sizeMb MB）→ $destRoot"
Write-Host "注意：runtime\ 已在 .gitignore 中，不进仓库；其他机器需自行跑本脚本。"
