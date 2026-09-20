# fetch-python-runtime.ps1 —— 把便携版 Python 拉进 runtime\python\
#
# 用途：Skill 里的 .py 脚本要能跑，但不能要求用户自己装 Python。
#       本脚本从国内镜像拉官方便携版（约 11MB），解压到 runtime\python\，随应用分发。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\fetch-python-runtime.ps1
#   powershell -ExecutionPolicy Bypass -File tools\fetch-python-runtime.ps1 -Version 3.13.12 -Force
#
# ⚠ 关键步骤：解压后必须删掉 python3xx._pth
#   官方包自带该文件会让 Python 进入 isolated 模式 —— 脚本所在目录不进 sys.path、
#   PYTHONPATH 被忽略、Lib\ 不加载。
#   实测（2026-09-20）：不删的话同目录模块 import 直接 ModuleNotFoundError，
#   local-rag 这类把公共逻辑拆在同目录模块的 Skill 跑不起来。
#   详见 docs\skill-runtime-plan.md §三。

param(
    [string]$Version = "3.13.12",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
$destRoot = Join-Path $repoRoot "runtime\python"
$zipPath  = Join-Path $env:TEMP "python-$Version-embed-amd64.zip"

Write-Host "目标目录：$destRoot"

if ((Test-Path (Join-Path $destRoot "python.exe")) -and -not $Force) {
    Write-Host "[skip] 已存在 python.exe，未做任何改动（要重拉请加 -Force）"
    exit 0
}

# 镜像顺序：国内优先，官方兜底
$urls = @(
    "https://mirrors.huaweicloud.com/python/$Version/python-$Version-embed-amd64.zip",
    "https://registry.npmmirror.com/-/binary/python/$Version/python-$Version-embed-amd64.zip",
    "https://www.python.org/ftp/python/$Version/python-$Version-embed-amd64.zip"
)

$downloaded = $false
foreach ($u in $urls) {
    try {
        Write-Host "[get ] $u"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-WebRequest -Uri $u -OutFile $zipPath -UseBasicParsing -TimeoutSec 300
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
    Write-Host "所有镜像均失败。请检查网络后重试。"
    exit 1
}

# 删除旧目录前先自保：路径必须落在本仓库的 runtime\python 下
if (-not $destRoot.EndsWith("runtime\python")) {
    Write-Host "内部错误：目标路径不符合预期（$destRoot），已中止以免误删。"
    exit 2
}
if (Test-Path $destRoot) {
    Remove-Item -LiteralPath $destRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $destRoot -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $destRoot -Force

# ── 删掉 _pth（isolated 模式开关），见文件头说明 ──
$pthFiles = Get-ChildItem -LiteralPath $destRoot -Filter "*._pth" -File
if ($pthFiles.Count -eq 0) {
    Write-Host "[warn] 没找到 ._pth 文件 —— 官方包结构可能变了，请人工确认 isolated 模式是否已默认关闭"
}
foreach ($f in $pthFiles) {
    Remove-Item -LiteralPath $f.FullName -Force
    Write-Host "[del ] $($f.Name)   （isolated 模式开关，必须删）"
}

# ── 自检：解释器能跑 + 同目录 import 能通 + 标准库齐 ──
$probeDir = Join-Path $env:TEMP ("fc-pyprobe-" + (Get-Random))
New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $probeDir "sib.py"), "V = 'ok'", (New-Object System.Text.UTF8Encoding($false)))
$probe = @'
import sys, json
r = {"py": sys.version.split()[0]}
try:
    import sib
    r["sibling_import"] = (sib.V == "ok")
except Exception:
    r["sibling_import"] = False
try:
    import json as _j, urllib.request, sqlite3, subprocess, pathlib, ssl
    r["stdlib"] = True
except Exception:
    r["stdlib"] = False
r["sys_path_len"] = len(sys.path)
print(json.dumps(r))
'@
[System.IO.File]::WriteAllText((Join-Path $probeDir "probe.py"), $probe, (New-Object System.Text.UTF8Encoding($false)))

$py = Join-Path $destRoot "python.exe"
$out = & $py (Join-Path $probeDir "probe.py") 2>&1
Write-Host "[self] $out"
Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue

if ($out -notmatch '"sibling_import":\s*true') {
    Write-Host ""
    Write-Host "自检未通过：同目录 import 不通 —— 多半是 ._pth 没删干净。"
    Write-Host "这会直接导致 local-rag 这类 Skill 无法运行，请先解决再继续。"
    exit 3
}

Write-Host ""
Write-Host "完成。运行时位置：$destRoot"
Write-Host "注意：runtime\ 已在 .gitignore 中，不进仓库；其他机器需自行跑本脚本。"
