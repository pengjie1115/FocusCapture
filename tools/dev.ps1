<#
  dev.ps1 - FocusCapture 开发流程入口（Agent 无关）

  用法：  tools\dev.ps1 <命令> [参数]
  自述：  tools\dev.ps1 help

  ── 设计原则（改本脚本前必读）──────────────────────────────
  1. 只放「动作」，不放「知识」。会变的东西（敏感文件清单、检查点条数、
     远程名、框架版本号）一律现场读取，绝不硬编码 —— 硬编码必然过期，
     而过期后的脚本会「照样跑得好好的，但判断已经错了」（软失真）。
  2. 退出码分级：0 = 成功 ／ 1 = 任务失败（该改代码）／ 2 = 脚本故障（该改脚本）。
  3. 界面文字一律 Write-Host；函数返回值只用于退出码，避免污染管道。
  4. 幂等：同一状态下跑两遍，结果一样。
  5. 不用 bash 核心命令（本机实测 mkdir/find/tail/head/ls/cp/grep/cat 会消失），
     一律用 PowerShell 原生 cmdlet。
  6. 兼容 PowerShell 5.1：不用 && / || / ?? / 三元运算符。
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$Arg1,

    [switch]$Apply,

    # 慢层开关。注意：AGENTS.md / REGRESSION.md / 本脚本 help 一直写的是 `test -Slow`，
    # 但顶层 param 里原先只有 -Apply，导致 `dev.ps1 test -Slow` **参数绑定直接失败且零输出**
    # （2026-09-20 实测：排查了半天，脚本看起来"什么都没干"）。这里补上 -Slow，
    # 并保留 -Apply 作为兼容别名 —— 文档说什么能跑，就该真能跑。
    [switch]$Slow,

    # 快层「全集」开关（2026-09-25 补）。背景：快层名义上叫"快"，实测 49.3 秒，其中 95% 压在
    # 5 个真做 I/O / 起进程的组上。分层后 `test`（默认）= 纯逻辑集（1 秒内），`test -All` = 全集。
    # **交付点（ready）内部固定用 -All**，所以交付时一条断言都不会少跑 —— 这里只改"什么时候跑"。
    [switch]$All,

    # start 的起点（2026-09-21 补）：原先写死从 main 开，而分支链场景（后一个 feature 从前一个开出）
    # 只能手敲 git —— 脚本能力与实际用法脱节。默认仍是 main，老用法不变。
    [string]$From = 'main'
)

# 注意：这里【不要】设 [Console]::OutputEncoding = UTF8。
# 本脚本是被父进程调用的（WorkBuddy 的 PowerShell 工具 / dev.bat），父进程按系统代码页（本机 GBK）
# 解码本脚本的输出；脚本若强设 UTF8 输出，父进程会按 GBK 解出乱码。
# 2026-09-20 实测：设了之后 `dev.ps1 status` 输出全是「鐜扮姸涓€灞?」这类乱码。
# 结论：输出保持系统默认编码，让调用方按同一编码解码。

$ExOk         = 0   # 成功
$ExTaskFail   = 1   # 任务失败：编译错 / 检查点红 —— 该改代码
$ExScriptFail = 2   # 脚本自身故障 —— 该改脚本（走「脚本异常报告」流程）

$RepoRoot = Split-Path -Parent $PSScriptRoot
$MsgFile  = Join-Path $RepoRoot '.git\FC_COMMIT_MSG'

# ────────────────────────── 工具函数 ──────────────────────────

function Repair-BuildEnv {
    # 本机实测：WorkBuddy 沙箱里这几个变量为空时，dotnet restore 会报
    #「Value cannot be null. (Parameter 'path1')」。只在为空时补，不覆盖真实值。
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        $env:APPDATA = Join-Path $env:USERPROFILE 'AppData\Roaming'
    }
    if ([string]::IsNullOrWhiteSpace($env:PROGRAMFILES)) { $env:PROGRAMFILES = 'C:\Program Files' }
    if ([string]::IsNullOrWhiteSpace($env:ProgramW6432)) { $env:ProgramW6432 = 'C:\Program Files' }
    if ([string]::IsNullOrWhiteSpace($env:ProgramData))  { $env:ProgramData  = 'C:\ProgramData' }
}

function Write-Head {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Write-Fail {
    param([string]$Step, [string]$Expect, [string]$Actual, [string]$Advice, [int]$Code)
    Write-Host ''
    Write-Host '[dev.ps1] 失败' -ForegroundColor Red
    Write-Host "  步骤：$Step"
    Write-Host "  期望：$Expect"
    Write-Host "  实际：$Actual"
    Write-Host "  建议：$Advice" -ForegroundColor Yellow
    if ($Code -eq $ExTaskFail) {
        Write-Host '  退出码：1（任务失败 —— 这是正常结果，去看输出改代码，不是改脚本）'
    } else {
        Write-Host '  退出码：2（脚本自身故障 —— 先绕过完成任务，再按「脚本异常报告」流程报告）'
    }
}

function Invoke-External {
    # 跑外部程序并返回退出码。两个坑（2026-09-20 实测，都真踩过）：
    #
    # ① 程序输出必须【先捕获、再输出】。若直接 `& prog` 让输出留在管道里，
    #    它会流进本函数的返回值流，和退出码混成一个数组 ——
    #    实测：检查点 31 项全过、退出码本是 0，结果 $code 变成「整段输出文本 + 0」，脚本误判成失败。
    #
    # ② .NET 系工具（dotnet build、检查点程序）按 UTF-8 输出，而本机控制台默认 GBK，
    #    按 GBK 解就会乱码（「正在确定要还原的项目」变成「姝ｅ湪纭畾瑕佽繕鍘熺殑椤圭洰」）。
    #    所以调用期间切 UTF-8 解码，捕获完成后【先恢复系统编码、再往外输出】——
    #    否则本脚本自己的 stdout 会写出 UTF-8 字节，父进程按 GBK 解，中文会再乱一次。
    #
    # 注：git 的界面消息以英文为主，按 UTF-8 解也正常；仓库内容（commit message）本就是 UTF-8，
    #     这也正是 Get-Git 用同一套做法的原因。
    param([string]$FilePath, [string[]]$Arguments)
    $oldEnc = [Console]::OutputEncoding
    $captured = @()
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $captured = @(& $FilePath @Arguments 2>&1)
        $code = $LASTEXITCODE
    } finally {
        [Console]::OutputEncoding = $oldEnc
    }
    foreach ($line in $captured) { Write-Host $line }
    return $code
}

function Get-Git {
    param([string[]]$Arguments)
    # 编码有两层，别混（2026-09-20 实测）：
    #   · 本脚本自己的中文输出 → 必须保持系统代码页（本机 GBK），因为父进程按 GBK 解码；
    #   · git 读出来的仓库内容（commit message、分支名等）在仓库里存的是 UTF-8 字节，
    #     若按 GBK 解就会变成「鏂板 dev.ps1」这类乱码。
    # 所以：仅在调用 git 这一段临时切成 UTF-8，调完立刻还原。
    $old = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $out = & git -C $RepoRoot @Arguments 2>&1
    } finally {
        [Console]::OutputEncoding = $old
    }
    return ($out | Out-String).Trim()
}

function Get-TrackedCount {
    $out = & git -C $RepoRoot ls-files 2>&1
    if ($null -eq $out) { return 0 }
    return @($out).Count
}

# ────────────────────────── 1. build ──────────────────────────

function Invoke-Build {
    Repair-BuildEnv
    Write-Head 'build 编译'
    Push-Location $RepoRoot
    try {
        $code = Invoke-External 'dotnet' @('build', '-c', 'Debug', '--nologo')
    } finally {
        Pop-Location
    }
    if ($code -ne 0) {
        Write-Fail 'dotnet build' '退出码 0' "退出码 $code" `
            '看上面的编译错误，多半是代码问题；若报 path1 为 null 则是环境变量没补上（跑 dev.ps1 status 看看）' $ExTaskFail
        return $ExTaskFail
    }
    Write-Host 'build 通过' -ForegroundColor Green
    return $ExOk
}

# ────────────────────────── 2. test ──────────────────────────

function Invoke-Test {
    param([switch]$Slow, [switch]$All)
    $name = 'test 快层(默认集)'
    $bat  = Join-Path $RepoRoot 'tests\run-tests.bat'
    $batArg = @()
    if ($Slow) {
        $name = 'test 慢层'
        $bat  = Join-Path $RepoRoot 'tests\sync\run-sync-tests.bat'
    }
    elseif ($All) {
        # 交付点一律跑全集：默认集 + 越界组（[5][6][8][9][10] —— 真剪贴板 / 真文件树 / 真子进程 / 本地 HTTP）。
        # 2026-09-25 分层：这五个组占快层总耗时 95%，拆出去后中间迭代只等 1 秒内，且一条断言不少跑。
        $name = 'test 快层(全集)'
        $batArg = @('--all')
    }
    if (-not (Test-Path -LiteralPath $bat)) {
        Write-Fail $name '检查点脚本存在' "找不到 $bat" '检查点脚本被移动或删除；确认后改本脚本' $ExScriptFail
        return $ExScriptFail
    }
    Write-Head $name

    $oldEnc = [Console]::OutputEncoding
    $captured = @()
    try {
        # 检查点程序按 UTF-8 输出（.NET 默认），而控制台默认 GBK
        # → 中文用例名会乱码（「加密解密」显示成「鍔犲瘑瑙ｅ瘑」），故调用期间临时切成 UTF-8 解码。
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        # 检查点 .bat 末尾带 pause：喂一个空行，避免非交互场景卡住等按键。
        $captured = @('' | & $bat @batArg 2>&1)
        $code = $LASTEXITCODE
    } finally {
        # 关键：先把系统编码恢复回来，再往外输出。
        # 若在 UTF-8 状态下直接输出，本脚本的 stdout 会写出 UTF-8 字节，
        # 而父进程按 GBK 解码 → 中文会又乱一次（2026-09-20 实测踩到）。
        [Console]::OutputEncoding = $oldEnc
    }
    foreach ($line in $captured) { Write-Host $line }

    # 双重判据（2026-09-20 补）：退出码 + 输出里的结论行，两个都要对。
    # 为什么不能只看退出码：tests 下两个 .bat 之前以 pause 结尾、从不传 ERRORLEVEL，
    # 于是退出码恒为 0 —— 这个门禁永远红不了，AGENTS.md 那条「退出码 0 = 检查点全过」是假的。
    # 「能用产物证明的就不信声明的退出码」是本项目一贯判据（见 snap 那段注释），这里沿用：
    # 结论行是检查点程序自己打的，比 .bat 传上来的数字更接近事实。
    $passed = @($captured | Where-Object { [string]$_ -match '\[RESULT\] ALL CHECKS PASSED' }).Count -gt 0
    if ($code -ne 0 -or -not $passed) {
        $why = if ($code -ne 0) { "退出码 $code" } else { '退出码 0，但输出里没有 [RESULT] ALL CHECKS PASSED' }
        Write-Fail $name '退出码 0 且输出含 [RESULT] ALL CHECKS PASSED' $why '检查点红了 —— 去看上面哪几条失败，改代码，不要改检查点标准' $ExTaskFail
        return $ExTaskFail
    }
    Write-Host "$name 通过" -ForegroundColor Green
    return $ExOk
}

# ────────────────────────── 3. ready ──────────────────────────

function Test-DocRefs {
    # 只扫仓库根目录的 md（docs/ 与 .workbuddy/ 里可能有「故意提到不存在文件」的归档文字，避免误报）
    $docs = @('AGENTS.md', 'REGRESSION.md', 'README.md', 'CHANGELOG.md', 'MIGRATION.md')
    $bad = @()
    $total = 0
    foreach ($d in $docs) {
        $p = Join-Path $RepoRoot $d
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $text = Get-Content -LiteralPath $p -Raw -Encoding UTF8
        # 只认「全 ASCII 的项目文档名」（AGENTS.md / docs/ARCHIVE-BRANCHES.md 这类）。
        # 不能贪心匹配任意 `xxx.md`：MIGRATION.md 里举例用到的数据文件名
        # （如 `灵感_2026-09-15.md`）会被误判成失效引用，导致 ready 假红 —— 狼来了会让门禁失效。
        $m = [regex]::Matches($text, '`([A-Za-z0-9][A-Za-z0-9._/\-]*\.md)`')
        # 外部生态的格式文件名不算项目引用 —— 它们必然出现在文档里，但不是本仓库的文件。
        # 2026-09-20 实测：B-17 段写 SKILL.md（Skill 机制约定的入口文件名）触发了假红。
        # 判据保持不变（宁可漏报也不扩大匹配面），只加已知的外部名白名单。
        $foreignNames = @('SKILL.md', 'YYYY-MM-DD.md')
        foreach ($one in $m) {
            $target = $one.Groups[1].Value
            if ($foreignNames -contains $target) { continue }
            $total = $total + 1
            $tp = Join-Path $RepoRoot ($target -replace '/', '\')
            if (-not (Test-Path -LiteralPath $tp)) {
                # 候选目录：仓库根找不到时再试开发记忆目录。AGENTS.md「三、开发记忆」用裸文件名
                # 引用 NOW/DECISIONS/PITFALLS 等（短名是刻意设计），2026-09-24 check-docs 落地时补的解析。
                $mem = Join-Path $RepoRoot ('.workbuddy\memory\' + ($target -replace '/', '\'))
                if (-not (Test-Path -LiteralPath $mem)) {
                    $bad += "$d -> $target"
                }
            }
        }
    }
    return @{ Total = $total; Bad = $bad }
}

function Invoke-Ready {
    Write-Head 'ready 交付前总检'
    $c1 = Invoke-Build
    if ($c1 -ne $ExOk) { return $c1 }

    # 交付点必须跑快层「全集」：默认集只是中间迭代的快速反馈，交付要一条不少地全跑（--all）。
    $c2 = Invoke-Test -All
    if ($c2 -ne $ExOk) { return $c2 }

    $c3 = Invoke-Test -Slow
    if ($c3 -ne $ExOk) { return $c3 }

    Write-Head '文档引用检查'
    $r = Test-DocRefs
    if ($r.Bad.Count -gt 0) {
        Write-Host "发现 $($r.Bad.Count) 处失效引用：" -ForegroundColor Yellow
        foreach ($b in $r.Bad) { Write-Host "  - $b" -ForegroundColor Yellow }
        Write-Fail '文档引用检查' '所有 ``xxx.md`` 引用都能找到目标' "失效 $($r.Bad.Count) 处" '改文档里的路径，或补回被引用文件；确认是合理例外后再说' $ExTaskFail
        return $ExTaskFail
    }
    Write-Host "文档引用检查通过（$($r.Total) 处全部有效）" -ForegroundColor Green

    Write-Host ''
    Write-Host '── ready 总检通过 ──' -ForegroundColor Green
    Write-Host '交付时请把上面这段完整贴给用户。'
    return $ExOk
}

# ────────────────────────── 4. check-docs ──────────────────────────

function Invoke-CheckDocs {
    # 独立的文档引用检查入口（复用 ready 内嵌的 Test-DocRefs，检查逻辑只此一份）。
    # 2026-09-24 补实现：设计文档七子命令里承诺过，此前从未落地（EXIT=2 未知命令）。
    Write-Head 'check-docs 文档引用检查'
    $r = Test-DocRefs
    if ($r.Bad.Count -gt 0) {
        Write-Host "发现 $($r.Bad.Count) 处失效引用：" -ForegroundColor Yellow
        foreach ($b in $r.Bad) { Write-Host "  - $b" -ForegroundColor Yellow }
        Write-Fail '文档引用检查' '所有 ``xxx.md`` 引用都能找到目标' "失效 $($r.Bad.Count) 处" '改文档里的路径，或补回被引用文件；确认是合理例外后再说' $ExTaskFail
        return $ExTaskFail
    }
    Write-Host "文档引用检查通过（$($r.Total) 处全部有效）" -ForegroundColor Green
    return $ExOk
}

# ────────────────────────── 5. push ──────────────────────────

function Invoke-Push {
    Write-Head 'push 双远程推送（目标：main）'

    $branch = Get-Git @('branch', '--show-current')
    if ($branch -ne 'main') {
        Write-Host "  当前分支：$branch（推的是 main，不是当前分支）" -ForegroundColor Yellow
    }
    $dirty = Get-Git @('status', '--porcelain')
    if ($dirty -ne '') {
        Write-Host '  注意：工作区有未提交改动 —— 推送只含已提交内容，改动不会上去' -ForegroundColor Yellow
    }

    $ahead = Get-Git @('log', '--oneline', 'origin/main..main')
    if ($ahead -eq '') {
        Write-Host '  本地 main 没有待推送提交（与 origin/main 一致）'
    }

    foreach ($remote in @('origin', 'github')) {
        Write-Host ''
        Write-Host "  → 推 $remote ..." -ForegroundColor Cyan

        # 自行捕获输出（而不是走 Invoke-External）：这里需要【看输出是否为空】来区分两种失败。
        $captured = @()
        $code = 1
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $oldPushEnc = [Console]::OutputEncoding
            try {
                [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
                $captured = @(& git -C $RepoRoot push $remote main 2>&1)
                $code = $LASTEXITCODE
            } finally {
                [Console]::OutputEncoding = $oldPushEnc
            }
            foreach ($line in $captured) { Write-Host $line }
            if ($code -eq 0) { break }
            if ($attempt -eq 1) {
                Write-Host "  $remote 第一次失败，隔 3 秒重试 ..." -ForegroundColor Yellow
                Start-Sleep -Seconds 3
            }
        }

        if ($code -ne 0) {
            Write-Host ''
            Write-Host '[dev.ps1] 失败' -ForegroundColor Red
            Write-Host "  步骤：git push $remote main"
            if ($captured.Count -eq 0) {
                # 【零输出 + 非零退出码】= 沙箱里「系统 git 出不了网」的典型特征。
                # 2026-09-20 实测：PowerShell 会话用的 git 是 C:\Program Files\Git\cmd\git.exe，
                # 同一时刻在 bash 里跑同样的命令完全正常（Gitee 一次成功），而系统 git 连错误都不吐。
                # 两类失败的区分判据就是「有没有输出」：有输出 = 真实网络问题；零输出 = 沙箱限制。
                Write-Host "  实际：退出码 $code，且 git 没有任何输出"
                Write-Host '  诊断：沙箱环境的已知限制 —— PowerShell 会话里的系统 git 出不了网。' -ForegroundColor Yellow
                Write-Host '        同一时刻在 bash 里跑同样的命令是正常的，所以这不是凭据问题、也不是仓库问题。' -ForegroundColor Yellow
                Write-Host '  建议：① Agent 改用 bash 执行 git push；② 或双击 tools\dev.bat push（本机终端无沙箱限制）；' -ForegroundColor Yellow
                Write-Host '        ③ 或你在本机终端手动推。' -ForegroundColor Yellow
            } else {
                Write-Host "  实际：退出码 $code"
                Write-Host '  建议：若是 GitHub 502 / CONNECT tunnel failed：本机实测是沙箱出口问题（对 Gitee 通畅、对 GitHub 时通时不通），' -ForegroundColor Yellow
                Write-Host '        别改 git 参数（http.version / http.proxy 都验证无效），隔一会儿直接重试即可；落后的提交一次能补完。' -ForegroundColor Yellow
            }
            Write-Host '  退出码：1（任务失败 —— 看上面的「建议」决定下一步，不是改代码）'
            return $ExTaskFail
        }
        Write-Host "  $remote 推送成功" -ForegroundColor Green
    }

    Write-Host ''
    Write-Host 'push 完成（两侧均已推送）' -ForegroundColor Green
    return $ExOk
}

# ────────────────────────── 5. recover ──────────────────────────

function Invoke-Recover {
    Write-Head 'recover git 索引异常诊断'

    $tracked = Get-TrackedCount
    Write-Host "  跟踪文件总数：$tracked"

    $status = Get-Git @('status', '--porcelain')
    $deletedStaged = 0
    $deletedWork   = 0
    $untracked     = @()
    foreach ($line in ($status -split "`n")) {
        if ($line.Trim() -eq '') { continue }
        if ($line.StartsWith('D ')) { $deletedStaged = $deletedStaged + 1 }
        elseif ($line.StartsWith(' D')) { $deletedWork = $deletedWork + 1 }
        elseif ($line.StartsWith('??')) { $untracked += $line.Substring(3).Trim().Trim('"') }
    }
    Write-Host "  已暂存删除（D ）：$deletedStaged"
    Write-Host "  工作区删除（ D）：$deletedWork"
    Write-Host "  未跟踪文件（??）：$($untracked.Count)"

    if ($deletedWork -eq 0 -and $deletedStaged -eq 0) {
        Write-Host ''
        Write-Host '  没有发现删除类异常，无需恢复。' -ForegroundColor Green
        return $ExOk
    }

    # 备份未跟踪文件 —— 本机踩过的坑：全树通配的 restore 会卷走「已暂存但 HEAD 没有」的文件，
    # 且索引损坏时 git status 本身不可信，所以先把 ?? 清单抄下来并备份出仓库。
    $backupRoot = Join-Path $env:TEMP ('fc-recover-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    if ($untracked.Count -gt 0) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        foreach ($u in $untracked) {
            $src = Join-Path $RepoRoot ($u -replace '/', '\')
            if (Test-Path -LiteralPath $src -PathType Leaf) {
                $dst = Join-Path $backupRoot ($u -replace '/', '\')
                $dstDir = Split-Path -Parent $dst
                if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
                Copy-Item -LiteralPath $src -Destination $dst -Force
            }
        }
        Write-Host ''
        Write-Host "  未跟踪文件已备份到：$backupRoot" -ForegroundColor Green
    }

    if (-not $Apply) {
        Write-Host ''
        Write-Host '  这是【干跑】模式，没有改动任何文件。' -ForegroundColor Yellow
        Write-Host '  确认要执行恢复，请加 -Apply：' -ForegroundColor Yellow
        Write-Host "      tools\dev.ps1 recover -Apply" -ForegroundColor Yellow
        Write-Host '  恢复命令是：git restore --source=HEAD --staged --worktree .'
        Write-Host '  （它是全树通配，只删「已暂存但 HEAD 没有」的 A 文件，不动 ?? —— 但仍需你确认）'
        return $ExOk
    }

    Write-Head '执行恢复'
    $code = Invoke-External 'git' @('-C', $RepoRoot, 'restore', '--source=HEAD', '--staged', '--worktree', '.')
    if ($code -ne 0) {
        Write-Fail 'git restore' '退出码 0' "退出码 $code" '恢复命令本身失败；把完整输出报给用户' $ExScriptFail
        return $ExScriptFail
    }

    $after = Get-TrackedCount
    Write-Host "  恢复后跟踪文件总数：$after（恢复前 $tracked）"
    if ($after -lt $tracked) {
        Write-Fail '恢复后校验' "跟踪文件数不少于 $tracked" "只有 $after" "有文件可能在恢复中丢失，备份在 $backupRoot" $ExScriptFail
        return $ExScriptFail
    }
    Write-Host ''
    Write-Host '  恢复完成，工作区应已干净。请跑一次 status 确认。' -ForegroundColor Green
    return $ExOk
}

# ────────────────────────── 6. snap ──────────────────────────

function Invoke-Snap {
    Write-Head 'snap 界面快照'
    $debugDir = Join-Path $RepoRoot 'bin\Debug'
    $exe = $null
    if (Test-Path -LiteralPath $debugDir) {
        $found = Get-ChildItem -LiteralPath $debugDir -Filter 'FocusCapture.exe' -Recurse -ErrorAction SilentlyContinue
        if ($found.Count -gt 0) { $exe = $found[0].FullName }
    }
    # 不硬编码 net8.0-windows 版本号 —— 上面用通配查找，框架升级也不用改脚本
    if ($null -eq $exe) {
        Write-Host '  未找到已编译的 exe，先编译 ...'
        $c = Invoke-Build
        if ($c -ne $ExOk) { return $c }
        $found = Get-ChildItem -LiteralPath $debugDir -Filter 'FocusCapture.exe' -Recurse -ErrorAction SilentlyContinue
        if ($found.Count -eq 0) {
            Write-Fail 'snap 定位 exe' 'bin\Debug 下存在 FocusCapture.exe' '没找到' '确认已编译；若框架版本变了本脚本无需改（用的是通配查找）' $ExScriptFail
            return $ExScriptFail
        }
        $exe = $found[0].FullName
    }

    $outDir = Join-Path $env:TEMP 'fc-ui-snapshot'
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    Write-Host "  用 exe：$exe"
    # 必须用 Start-Process -Wait（两个原因，都实测过）：
    #   ① FocusCapture.exe 是 GUI 子系统程序，用 `& exe` 调用时 PowerShell 不会等它跑完 ——
    #      实测脚本立刻去查目录是空的，而图是在脚本结束之后才写完的（25 张，时间戳对得上）；
    #   ② 这类程序的退出码不可靠（$LASTEXITCODE 为空），所以【不看退出码，看产物】：
    #      出图目录里出现 PNG 才算成功。能用产物证明的，就不信「声明的退出码」。
    $proc = Start-Process -FilePath $exe -ArgumentList @('--snapshot', '--out', $outDir) -Wait -PassThru
    if ($null -ne $proc -and $proc.ExitCode -ne 0) {
        Write-Host "  程序退出码：$($proc.ExitCode)（仅参考 —— GUI 程序退出码未必可靠，以下面的产物为准）" -ForegroundColor Yellow
    }

    $pngs = @(Get-ChildItem -LiteralPath $outDir -Filter '*.png' -File -ErrorAction SilentlyContinue)
    if ($pngs.Count -eq 0) {
        Write-Fail 'FocusCapture.exe --snapshot' '出图目录里出现 PNG' "一张图都没生成（$outDir）" `
            '看上面的程序输出；可能是窗口初始化失败' $ExTaskFail
        return $ExTaskFail
    }
    Write-Host "  已生成 $($pngs.Count) 张图" -ForegroundColor Green

    $log = Join-Path $outDir 'snapshot.log'
    Write-Host ''
    Write-Host "  出图目录：$outDir" -ForegroundColor Green
    if (Test-Path -LiteralPath $log) {
        Write-Host '  ── snapshot.log（尺寸异常是布局异常的第一信号）──'
        Get-Content -LiteralPath $log | ForEach-Object { Write-Host "  $_" }
    }
    Write-Host ''
    Write-Host '  判读提醒：不要只看缩略图，图标偏移/字形残缺要放大裁剪再看；对比法最有效（改动前后各跑一次）。' -ForegroundColor Yellow
    return $ExOk
}

# ────────────────────────── 7. status ──────────────────────────

function Invoke-Status {
    Write-Head 'status 现状一屏'

    Write-Host "  分支：$(Get-Git @('branch', '--show-current'))"

    $status = Get-Git @('status', '--porcelain')
    if ($status -eq '') { Write-Host '  工作区：干净' -ForegroundColor Green }
    else {
        $lines = @($status -split "`n" | Where-Object { $_.Trim() -ne '' })
        Write-Host "  工作区：$($lines.Count) 项改动" -ForegroundColor Yellow
        foreach ($l in $lines) { Write-Host "    $($l.TrimEnd())" }
    }

    $tracked = Get-TrackedCount
    Write-Host "  跟踪文件总数：$tracked"

    $aheadMain = Get-Git @('log', '--oneline', 'main..HEAD')
    if ($aheadMain -eq '') { Write-Host '  与 main：无差距' }
    else {
        $n = @($aheadMain -split "`n" | Where-Object { $_.Trim() -ne '' }).Count
        Write-Host "  与 main：领先 $n 个提交" -ForegroundColor Yellow
        $aheadMain -split "`n" | ForEach-Object { if ($_.Trim() -ne '') { Write-Host "    $_" } }
    }

    $unpushed = Get-Git @('log', '--oneline', 'origin/main..main')
    if ($unpushed -eq '') { Write-Host '  本地 main 待推送：无' -ForegroundColor Green }
    else {
        $n = @($unpushed -split "`n" | Where-Object { $_.Trim() -ne '' }).Count
        Write-Host "  本地 main 待推送：$n 个提交" -ForegroundColor Yellow
    }

    # 双远程可达性只做提示，不联网探测（GitHub 本机时通时不通，会拖慢 status）
    $remotes = Get-Git @('remote')
    Write-Host "  远程：$(($remotes -split "`n" | Where-Object { $_.Trim() -ne '' }) -join ' / ')"

    return $ExOk
}

# ────────────────────────── 8. start ──────────────────────────

function Invoke-Start {
    param([string]$Name)
    Write-Head 'start 开分支'

    if ([string]::IsNullOrWhiteSpace($Name)) {
        # 参数没给是【调用方】的问题，不是脚本坏了 → 用 1（任务失败），
        # 避免误触发「脚本异常报告」流程（那套流程是给真的脚本故障用的）。
        Write-Fail 'start 参数' 'dev.ps1 start <分支名>' '没给分支名' '例：dev.ps1 start 悬浮球角标修复' $ExTaskFail
        return $ExTaskFail
    }

    $prefixes = @('feature/', 'fix/', 'docs/', 'release/', 'experiment/')
    $hasPrefix = $false
    foreach ($p in $prefixes) { if ($Name.StartsWith($p)) { $hasPrefix = $true } }
    if (-not $hasPrefix) {
        $Name = 'feature/' + $Name
        Write-Host "  未带类型前缀，自动补为：$Name"
    }

    $dirty = Get-Git @('status', '--porcelain')
    if ($dirty -ne '') {
        Write-Fail '工作区检查' '开分支前工作区干净' '有未提交改动' '先提交或 stash，再开分支（否则改动会跟着切过去）' $ExTaskFail
        return $ExTaskFail
    }

    $exists = Get-Git @('branch', '--list', $Name)
    if ($exists -ne '') {
        Write-Fail '分支查重' "不存在同名分支 $Name" '已存在' '换个名字，或用 git checkout 切过去' $ExTaskFail
        return $ExTaskFail
    }

    # 开工前查重（规范要求）：看看 main 上有没有做过类似的事
    $keyword = ($Name -split '/' | Select-Object -Last 1)
    $similar = Get-Git @('log', '--all', '--oneline', "--grep=$keyword", '-20')
    if ($similar -ne '') {
        Write-Host "  ⚠ 历史提交里有提到「$keyword」的（防重复开发，请看一眼）：" -ForegroundColor Yellow
        $similar -split "`n" | ForEach-Object { if ($_.Trim() -ne '') { Write-Host "    $_" } }
    }

    $code = Invoke-External 'git' @('-C', $RepoRoot, 'checkout', '-b', $Name, $From)
    if ($code -ne 0) {
        Write-Fail "git checkout -b $Name $From" '退出码 0' "退出码 $code" '建分支失败；把输出报给用户' $ExScriptFail
        return $ExScriptFail
    }

    Write-Host ''
    Write-Host "  已从 $From 新建并切到：$Name" -ForegroundColor Green
    Write-Host '  开完立刻验一次工作区（本机 checkout 有触发索引异常的历史）：' -ForegroundColor Yellow
    return Invoke-Status
}

# ────────────────────────── 9. merge ──────────────────────────

function Invoke-Merge {
    Write-Head 'merge 合并到 main（ff-only）'

    $branch = Get-Git @('branch', '--show-current')
    if ($branch -eq 'main') {
        Write-Fail 'merge 前置检查' '当前不在 main 上' '已经在 main 上' '先切到要合并的分支，再跑 merge' $ExTaskFail
        return $ExTaskFail
    }

    $dirty = Get-Git @('status', '--porcelain')
    if ($dirty -ne '') {
        Write-Fail '工作区检查' '合并前工作区干净' '有未提交改动' '先提交，再合并' $ExTaskFail
        return $ExTaskFail
    }

    $before = Get-TrackedCount
    Write-Host "  待合并分支：$branch"
    Write-Host "  合并前跟踪文件总数：$before（仅参考 —— 切分支后文件数本来就会变）"
    Write-Host '  ⚠ 本机实测：git checkout 会触发「文件从磁盘消失」（索引完好、可恢复）。' -ForegroundColor Yellow
    Write-Host '     若下一步校验失败：先 git checkout 切回本分支，再 git restore --worktree .' -ForegroundColor Yellow

    $code = Invoke-External 'git' @('-C', $RepoRoot, 'checkout', 'main')
    if ($code -ne 0) {
        Write-Fail 'git checkout main' '退出码 0' "退出码 $code" '切分支失败；把输出报给用户' $ExScriptFail
        return $ExScriptFail
    }

    # ⚠ 判据只有一个：切换前工作区干净（上面已校验），切换后也该干净。
    #    不要用「文件数是否一致」判断 —— 切分支本来就会换一整套文件（各分支文件集不同）：
    #    实测 main 比本分支多 4 个（本分支删了 8 个、加了 4 个），文件数必然变化。
    #    2026-09-20 实测踩到：原判据把这种正常差异当成异常，报了假警报。
    $afterCheckout = Get-TrackedCount
    $st = Get-Git @('status', '--porcelain')
    $vanished = @()
    foreach ($line in ($st -split "`n")) {
        if ($line.Trim() -eq '') { continue }
        if ($line.StartsWith(' D')) { $vanished += $line.Substring(3).Trim() }
    }
    if ($vanished.Count -gt 0) {
        # 本机 checkout 会【稳定复现】这个现象（2026-09-20 实测：切到 main 丢 18 个、切回分支丢 8 个），
        # 所以不能只报警 —— 每次都报等于每次都卡住。按本方案自己的原则：
        # 【能让脚本自己修好的，绝不上升到 AI】。
        Write-Host ''
        Write-Host "  检测到 $($vanished.Count) 个文件被 checkout 弄丢（本机已知现象）：" -ForegroundColor Yellow
        foreach ($f in $vanished) { Write-Host "    $f" -ForegroundColor Yellow }
        Write-Host '  → 自动恢复（git restore --worktree：只写工作区，不动索引、不删文件）...' -ForegroundColor Yellow
        $rc = Invoke-External 'git' @('-C', $RepoRoot, 'restore', '--worktree', '.')
        $st2 = Get-Git @('status', '--porcelain')
        $still = @()
        foreach ($line in ($st2 -split "`n")) {
            if ($line.Trim() -eq '') { continue }
            if ($line.StartsWith(' D')) { $still += $line.Substring(3).Trim() }
        }
        if ($rc -ne 0 -or $still.Count -gt 0) {
            Write-Fail '自动恢复消失文件' '恢复后工作区干净' "仍剩 $($still.Count) 个文件缺失" `
                '自动恢复没成功。切回原分支后用 git restore --worktree . 手动恢复；或先跑 dev.ps1 recover 看现场' $ExTaskFail
            return $ExTaskFail
        }
        Write-Host "  已自动恢复 $($vanished.Count) 个文件，继续合并" -ForegroundColor Green
    }
    Write-Host "  切到 main 后校验通过（工作区干净；文件数 $before -> $afterCheckout，差异属正常）" -ForegroundColor Green

    $code = Invoke-External 'git' @('-C', $RepoRoot, 'merge', '--ff-only', $branch)
    if ($code -ne 0) {
        Write-Fail "git merge --ff-only $branch" '退出码 0' "退出码 $code" `
            'ff-only 失败一般意味着 main 上有该分支没有的提交。若确认要真合并，请人工判断（本脚本不做非快进合并）' $ExTaskFail
        return $ExTaskFail
    }

    $after = Get-TrackedCount
    Write-Host ''
    Write-Host "  合并完成：$before -> $after 个跟踪文件" -ForegroundColor Green
    Write-Host '  提醒：分支按规范「合并即删」（git log 即归档，不登记）。删分支前先跑 status 确认。' -ForegroundColor Yellow
    Write-Host '  另注：若本脚本此前只存在于被合并分支上，这是它第一次进 main。' -ForegroundColor DarkGray
    return $ExOk
}

# ────────────────────────── 10. commit ──────────────────────────

function Invoke-Commit {
    Write-Head 'commit 提交'

    if (-not (Test-Path -LiteralPath $MsgFile)) {
        # 缺提交信息是【调用方没准备好】→ 1，不是脚本故障
        Write-Fail '读提交信息' "存在 $MsgFile" '没找到提交信息文件' `
            "用编辑器 / Write 工具把提交信息（UTF-8）写到 $MsgFile，再跑本命令。中文不要走命令行参数 —— 本机实测会被 GBK 破坏" $ExTaskFail
        return $ExTaskFail
    }

    $msg = Get-Content -LiteralPath $MsgFile -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($msg)) {
        Write-Fail '读提交信息' '提交信息非空' '文件是空的' "把内容写进 $MsgFile" $ExTaskFail
        return $ExTaskFail
    }

    $cached = Get-Git @('diff', '--cached', '--stat')
    if ($cached -eq '') {
        Write-Fail '暂存检查' '有已暂存改动' '暂存区是空的' `
            '先用 git add <明确路径> 暂存（禁止 git add -A，见 AGENTS.md 红线 3）。暂存后用 git diff --cached --stat 复查一遍' $ExTaskFail
        return $ExTaskFail
    }

    Write-Host '  ── 本次将提交（请核对，防止漏 add 文件）──'
    $cached -split "`n" | ForEach-Object { if ($_.Trim() -ne '') { Write-Host "  $_" } }
    Write-Host ''
    Write-Host '  ── 提交信息 ──'
    $msg -split "`n" | ForEach-Object { Write-Host "  $_" }
    Write-Host ''

    $code = Invoke-External 'git' @('-C', $RepoRoot, 'commit', '-F', $MsgFile)
    if ($code -ne 0) {
        Write-Fail 'git commit -F' '退出码 0' "退出码 $code" '提交失败；看上面输出（可能是 hook 拦下或没暂存内容）' $ExTaskFail
        return $ExTaskFail
    }
    Write-Host "  提交成功。提交信息文件仍在 $MsgFile，下次会被覆盖。" -ForegroundColor Green
    return $ExOk
}

# ────────────────────────── help ──────────────────────────

function Show-Help {
    Write-Host ''
    Write-Host 'dev.ps1 - FocusCapture 开发流程入口（Agent 无关）' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '用法：tools\dev.ps1 <命令> [参数]'
    Write-Host ''
    Write-Host '命令：'
    Write-Host '  build                 编译 Debug（自带环境变量补丁）'
    Write-Host '  test                  跑快层「默认集」——纯逻辑，1 秒内（中间迭代用这个）'
    Write-Host '  test -All             跑快层「全集」——默认集 + 越界组（真 I/O / 真子进程）'
    Write-Host '  test -Slow            跑慢层检查点'
    Write-Host '  ready                 交付前总检：编译 + 快层全集 + 慢层 + 文档引用检查'
    Write-Host '  check-docs            单独跑文档引用检查（改文档后快速验证；全量仍在 ready）'
    Write-Host '  status                一屏现状：分支 / 改动 / 与 main 差距 / 待推送 / 文件数'
    Write-Host '  start <分支名>        新建分支（自动补类型前缀 + 开工查重；默认从 main，-From 指定起点）'
    Write-Host '  merge                 把当前分支 ff-only 合并到 main（合并后自动校验索引）'
    Write-Host '  push                  推 main 到双远程（origin=Gitee, github）'
    Write-Host '  recover               诊断 git 索引异常（默认只诊断+备份，加 -Apply 才恢复）'
    Write-Host '  snap                  出界面快照到 %TEMP%\fc-ui-snapshot 并打印尺寸表'
    Write-Host '  commit                提交（从 .git\FC_COMMIT_MSG 读信息，避免中文走命令行）'
    Write-Host '  help                  显示本帮助'
    Write-Host ''
    Write-Host '退出码约定（重要）：' -ForegroundColor Yellow
    Write-Host '  0 = 成功'
    Write-Host '  1 = 任务失败（编译错 / 检查点红）—— 这是正常结果，去看输出改代码，不是改脚本'
    Write-Host '  2 = 脚本自身故障 —— 先绕过把任务做完，交付时附「脚本异常报告」，再问用户改不改脚本'
    Write-Host ''
    Write-Host '提交信息文件：.git\FC_COMMIT_MSG（UTF-8，用文件传中文，不要走命令行参数）'
    Write-Host ''
    Write-Host '细节见 docs\dev-script-plan.md。' -ForegroundColor DarkGray
}

# ────────────────────────── 分发 ──────────────────────────

$final = $ExOk
try {
    switch ($Command.ToLower()) {
        'build'   { $final = Invoke-Build }
        'test'    {
            if ($Slow -or $Apply) { $final = Invoke-Test -Slow }
            elseif ($All) { $final = Invoke-Test -All }
            else { $final = Invoke-Test }
        }
        'ready'   { $final = Invoke-Ready }
        'check-docs' { $final = Invoke-CheckDocs }
        'push'    { $final = Invoke-Push }
        'recover' { $final = Invoke-Recover }
        'snap'    { $final = Invoke-Snap }
        'status'  { $final = Invoke-Status }
        'start'   { $final = Invoke-Start -Name $Arg1 }
        'merge'   { $final = Invoke-Merge }
        'commit'  { $final = Invoke-Commit }
        'help'    { Show-Help }
        '-h'      { Show-Help }
        '--help'  { Show-Help }
        default {
            Write-Host "未知命令：$Command" -ForegroundColor Red
            Show-Help
            $final = $ExScriptFail
        }
    }
} catch {
    Write-Host ''
    Write-Host '[dev.ps1] 脚本自身异常' -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)"
    $final = $ExScriptFail
}

exit $final
