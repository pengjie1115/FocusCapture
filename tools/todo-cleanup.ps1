<#
  todo-cleanup.ps1 — 笔记/待办重复行清理工具（2026-09-12 行身份改造配套）

  为什么需要它：
    修复前，待办"标记完成"后云端旧版本行会反复投递回本地，导致同一条待办在 md 文件里堆出多份
    （已办副本 + 复活出来的未办行）。代码修好后不会再产生新的重复，但**已经存在**的重复行需要清一次。

  它做什么：
    1) 找出"同一条待办"的多个副本（判定键 = 行文本去掉「状态: 已办/已读」属性后的归一化文本，
       即未办/已办/已读三种版本视为同一条；普通笔记按整行文本判重）
    2) 每组保留一份，删除其余（优先保留「已办」那份 —— 与用户"我点过已完成"的意图一致；
       保留项可随时在应用里右键切换回未办）
    3) 优先保留落在"自然归属文件"里的那份（待办=提醒日文件，笔记=行时间戳日期文件）

  安全设计：
    - 默认 **只报告，不改任何文件**（预览模式）
    - 加 -Apply 才真正执行，且执行前把受影响的 md 文件整份备份到 .cleanup-backup-<时间戳>\
    - 只处理指定笔记目录内的 *.md，绝不碰别处；不删文件，只删文件里的重复行
    - 执行后自动复核（再扫一遍确认无重复），有异常如实报出

  用法（在项目根目录或任意位置）：
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\todo-cleanup.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\todo-cleanup.ps1 -Apply
    双击 tools\run-todo-cleanup.bat = 预览模式
#>
param(
    [string]$NotesPath = "",
    [switch]$Apply,
    [switch]$IncludeNotes
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "===== 笔记/待办重复行清理工具 =====" -ForegroundColor Cyan

# ── 1. 定位笔记目录 ──
if ([string]::IsNullOrWhiteSpace($NotesPath)) {
    $settingsPath = Join-Path $env:APPDATA "FocusCapture\settings.json"
    if (-not (Test-Path $settingsPath)) {
        Write-Host "[X] 找不到 $settingsPath，请用 -NotesPath 显式指定笔记目录" -ForegroundColor Red
        exit 2
    }
    $settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $NotesPath = $settings.NotesPath
}
if ([string]::IsNullOrWhiteSpace($NotesPath) -or -not (Test-Path $NotesPath)) {
    Write-Host "[X] 笔记目录不存在：$NotesPath" -ForegroundColor Red
    exit 2
}
$NotesPath = (Resolve-Path $NotesPath).Path
if ($NotesPath.Length -le 3) {
    Write-Host "[X] 目录过于宽泛（疑似盘符根目录），拒绝执行：$NotesPath" -ForegroundColor Red
    exit 2
}

Write-Host "笔记目录：$NotesPath"
Write-Host ("模式：" + $(if ($Apply) { "**执行修改**（会先备份）" } else { "预览（不改任何文件）" }))
Write-Host ""

# ── 2. 扫描全部 md，按"逻辑身份"分组 ──
function Get-IdealFile([string]$line) {
    $due = [regex]::Match($line, '提醒: (\d{4}-\d{2}-\d{2})')
    if ($due.Success) { return ("灵感_" + $due.Groups[1].Value + ".md") }
    $ts = [regex]::Match($line, '^- \[(\d{4}-\d{2}-\d{2})')
    if ($ts.Success) { return ("灵感_" + $ts.Groups[1].Value + ".md") }
    return ""
}

$files = Get-ChildItem -Path $NotesPath -Filter *.md -File | Sort-Object Name
$records = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i].TrimEnd("`r")
        if ($l -notmatch '^- \[') { continue }
        $isDone = ($l -match '状态: (已办|已读)')
        $isTodo = ($l -match '【待办】')
        # 判定键：去掉状态属性后归一化（未办/已办/已读 三种版本归为同一条）
        $key = $l -replace ',\s*状态: (已办|已读)', '' -replace '\(状态: (已办|已读)\)', ''
        $key = ($key -replace '\s+', ' ').Trim()
        $null = $records.Add([pscustomobject]@{
            File = $f.FullName
            FileName = $f.Name
            Index = $i
            Raw = $l
            Key = $key
            IsDone = $isDone
            IsTodo = $isTodo
            Ideal = (Get-IdealFile $l)
        })
    }
}

if (-not $IncludeNotes) {
    # 默认只处理待办（普通笔记的整行判重仍会命中，但历史上没出现过重复笔记副本）
    $targets = $records | Where-Object { $_.IsTodo }
} else {
    $targets = $records
}

$groups = $targets | Group-Object -Property Key | Where-Object { $_.Count -gt 1 }
if (-not $groups) {
    Write-Host "没有发现重复行 —— 无需清理。" -ForegroundColor Green
    exit 0
}

# ── 3. 每组挑一个保留，其余待删 ──
$toDelete = New-Object System.Collections.ArrayList
$groupNo = 0
foreach ($g in $groups) {
    $groupNo++
    $sorted = $g.Group | Sort-Object -Property `
        @{ Expression = { if ($_.IsDone) { 0 } else { 1 } } }, `
        @{ Expression = { if ($_.Ideal -ne "" -and $_.FileName -eq $_.Ideal) { 0 } else { 1 } } }, `
        @{ Expression = { $_.Index } }
    $keep = $sorted[0]
    $drop = $sorted | Select-Object -Skip 1

    $title = $g.Name
    if ($title.Length -gt 62) { $title = $title.Substring(0, 62) + "…" }
    Write-Host ("[" + $groupNo + "] " + $title)
    Write-Host ("      保留：" + $keep.FileName + " 第 " + ($keep.Index + 1) + " 行  (" + $(if ($keep.IsDone) { "已办" } else { "未办" }) + ")") -ForegroundColor Green
    foreach ($d in $drop) {
        Write-Host ("      删除：" + $d.FileName + " 第 " + ($d.Index + 1) + " 行  (" + $(if ($d.IsDone) { "已办" } else { "未办" }) + ")") -ForegroundColor Yellow
        $null = $toDelete.Add($d)
    }
}

Write-Host ""
Write-Host ("共 " + $groups.Count + " 组重复，待删除 " + $toDelete.Count + " 行。" ) -ForegroundColor Cyan

if (-not $Apply) {
    Write-Host ""
    Write-Host "以上为预览，未做任何修改。" -ForegroundColor Cyan
    Write-Host "确认无误后执行（会先整份备份受影响的 md 文件）：" -ForegroundColor Cyan
    Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File tools\todo-cleanup.ps1 -Apply" -ForegroundColor White
    exit 0
}

# ── 4. 备份 + 执行 ──
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $NotesPath (".cleanup-backup-" + $stamp)
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

$affectedFiles = $toDelete | Select-Object -ExpandProperty File -Unique
$batch = 0
foreach ($file in $affectedFiles) {
    $batch++
    if ($batch % 10 -eq 1) { Write-Host ("  备份并处理第 " + $batch + " 个文件…") }
    $name = [System.IO.Path]::GetFileName($file)
    Copy-Item -Path $file -Destination (Join-Path $backupDir $name) -Force
}

$totalRemoved = 0
foreach ($file in $affectedFiles) {
    $dropIndexes = @()
    foreach ($d in $toDelete) { if ($d.File -eq $file) { $dropIndexes += $d.Index } }
    $lines = [System.IO.File]::ReadAllLines($file, [System.Text.Encoding]::UTF8)
    $kept = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($dropIndexes -contains $i) { continue }
        $null = $kept.Add($lines[$i])
    }
    [System.IO.File]::WriteAllLines($file, $kept.ToArray(), (New-Object System.Text.UTF8Encoding($true)))
    $totalRemoved += $dropIndexes.Count
}

# ── 5. 复核 ──
$left = 0
foreach ($file in (Get-ChildItem -Path $NotesPath -Filter *.md -File)) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
    $keys = New-Object System.Collections.ArrayList
    foreach ($l in $lines) {
        if ($l -notmatch '^- \[') { continue }
        if (-not ($l -match '【待办】')) { continue }
        $k = $l -replace ',\s*状态: (已办|已读)', '' -replace '\(状态: (已办|已读)\)', ''
        $null = $keys.Add((($k -replace '\s+', ' ').Trim()))
    }
    $dups = $keys | Group-Object | Where-Object { $_.Count -gt 1 }
    $left += @($dups).Count
}

Write-Host ""
Write-Host ("已删除 " + $totalRemoved + " 行重复。备份目录：" + $backupDir) -ForegroundColor Green
if ($left -eq 0) {
    Write-Host "复核通过：待办重复行已清零。" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("[!] 复核发现仍有 " + $left + " 组重复，请把上面输出发给 AI 排查。") -ForegroundColor Red
    exit 1
}
