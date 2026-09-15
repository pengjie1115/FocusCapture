# ============================================================
#  百度网盘开放平台 . 下载速度实测
# ------------------------------------------------------------
#  用途：应用建好后实测开放 API 的真实下载速度，
#        用于判断「百度网盘本地缓存方案」值不值得开发。
#
#  为什么要单独测：
#    百度网盘客户端有 P2P / 多线程 / 协议优化，API 只有单链接 HTTP，
#    两者速度不是一回事。客户端测速只能当上限参考，不能当作结论。
#
#  凭据处理：
#    AppKey / SecretKey / access_token 只在本次运行的内存里使用，
#    不写入任何文件，也不打印到屏幕。
#
#  用法：
#    双击同目录下的 baidu-speedtest.bat
#    或执行 powershell -ExecutionPolicy Bypass -File baidu-speedtest.ps1
#
#  前置条件：
#    1. 已在 https://pan.baidu.com/union/ 创建「个人使用」应用
#    2. 已往「我的应用数据 / 你的应用名」里放了一个大文件（建议 >100MB）
# ============================================================

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$OAuthBase = "https://openapi.baidu.com/oauth/2.0"
$PanBase   = "https://pan.baidu.com/rest/2.0"
$UserAgent = "pan.baidu.com"
$TestSecs  = 10

function Say($text, $color) {
    if ($color) { Write-Host $text -ForegroundColor $color } else { Write-Host $text }
}
function Head($text) {
    Write-Host ""
    Write-Host "---- $text ----" -ForegroundColor Cyan
}
function Fail($text) {
    Write-Host ""
    Write-Host "  [失败] $text" -ForegroundColor Red
    exit 1
}
function Note($text) {
    Write-Host "  $text" -ForegroundColor DarkGray
}

# 从异常里取出服务端返回的正文（百度的错误信息都在 body 里）
function Get-ErrorBody($err) {
    try {
        $resp = $err.Exception.Response
        if ($resp -ne $null) {
            $stream = $resp.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            return $reader.ReadToEnd()
        }
    } catch { }
    return ""
}

Write-Host ""
Say "============================================" White
Say "  百度网盘开放平台 . 下载速度实测" White
Say "============================================" White
Note "凭据只在本次运行中使用，不写入任何文件"

# ---------- 1. 凭据 ----------
# 用「复制 + 回车」代替手工输入：密钥是 32 位随机串，手打或粘贴进隐蔽输入框都极易出错
function Read-CredentialFromClipboard($label, $expectLen) {
    while ($true) {
        Write-Host ("  请复制【" + $label + "】到剪贴板（在网页上选中后 Ctrl+C），然后按回车")
        Read-Host "  " | Out-Null

        $v = ""
        try {
            $v = Get-Clipboard -Raw
        } catch {
            Write-Host ("  [!] 读剪贴板失败，改为手动粘贴（屏幕上会显示出来，这是正常的）") -ForegroundColor Yellow
            $v = Read-Host ("  粘贴 " + $label)
        }
        if ($v -eq $null) { $v = "" }
        $v = $v.Trim()

        if ([string]::IsNullOrWhiteSpace($v)) {
            Write-Host "  [x] 剪贴板是空的，请重新复制" -ForegroundColor Red
            Write-Host ""
            continue
        }
        if ($v -match "\s") {
            Write-Host "  [x] 内容里有空格或换行，像是复制多了，请只复制那一串字符" -ForegroundColor Red
            Write-Host ""
            continue
        }
        if ($expectLen -gt 0 -and $v.Length -ne $expectLen) {
            Write-Host ("  [!] 读到 " + $v.Length + " 位，正常应为 " + $expectLen + " 位") -ForegroundColor Yellow
            Write-Host "      [直接回车] 重新复制     [输入 y] 就用这个" -ForegroundColor Yellow
            $confirm = Read-Host "  "
            if ($confirm -ne "y") { continue }
        }
        $head = $v.Substring(0, [Math]::Min(4, $v.Length))
        Write-Host ("  [√] " + $label + " 已读取：" + $head + "****（共 " + $v.Length + " 位）") -ForegroundColor Green
        return $v
    }
}

Head "1 / 5  输入应用凭据"
Note "在 https://pan.baidu.com/union/console/applist 点开你的应用即可看到这两个值"
Write-Host ""

$appKey = Read-CredentialFromClipboard "AppKey" 32
$appSecret = Read-CredentialFromClipboard "SecretKey" 32

# ---------- 2. 授权 ----------
Head "2 / 5  授权（浏览器里点同意）"
$authUrl = "$OAuthBase/authorize?response_type=code&client_id=$appKey&redirect_uri=oob&scope=basic,netdisk"
Note "即将打开浏览器，登录百度账号后点「同意授权」"
Note "授权完成后页面会显示一串授权码，复制它"
Write-Host ""
Read-Host "  准备好后按回车打开浏览器"
Start-Process $authUrl | Out-Null
Write-Host ""
$code = Read-Host "  粘贴授权码"
$code = $code.Trim()
if ([string]::IsNullOrWhiteSpace($code)) { Fail "授权码为空" }

Write-Host ""
Note "正在用授权码换取 access_token ..."
$tokenUrl = "$OAuthBase/token?grant_type=authorization_code&code=$code&client_id=$appKey&client_secret=$appSecret&redirect_uri=oob"
try {
    $tok = Invoke-RestMethod -Uri $tokenUrl -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30
} catch {
    $body = Get-ErrorBody $_
    Fail ("换取 token 失败。" + $body + "`n         常见原因：授权码已用过（只能用一次，10 分钟过期），或 AppKey 与 SecretKey 不匹配")
}
if ([string]::IsNullOrWhiteSpace($tok.access_token)) {
    Fail ("响应里没有 access_token：" + ($tok | ConvertTo-Json -Compress))
}
$token = $tok.access_token
Write-Host ""
Say ("  [成功] 已获得 access_token（有效期 " + [Math]::Round($tok.expires_in / 86400, 1) + " 天）") Green

function Api-List($dir) {
    $enc = (($dir -split "/") | ForEach-Object { [uri]::EscapeDataString($_) }) -join "/"
    $url = "$PanBase/xpan/file?method=list&access_token=$token&dir=$enc&order=size&desc=1&limit=1000"
    return Invoke-RestMethod -Uri $url -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30
}

# ---------- 3. 定位沙箱目录 ----------
Head "3 / 5  定位应用沙箱目录"
$appName = ""
try {
    $apps = Api-List "/apps"
    if ($apps.errno -eq 0 -and $apps.list -ne $null) {
        $dirs = @($apps.list | Where-Object { $_.isdir -eq 1 })
        if ($dirs.Count -eq 1) {
            $appName = $dirs[0].server_filename
            Say ("  [自动识别] 应用名：" + $appName) Green
        } elseif ($dirs.Count -gt 1) {
            Write-Host "  发现多个应用目录："
            for ($i = 0; $i -lt $dirs.Count; $i++) {
                Write-Host ("    [" + $i + "] " + $dirs[$i].server_filename)
            }
            $pick = Read-Host "  输入编号"
            $appName = $dirs[[int]$pick].server_filename
        }
    }
} catch { }

if ([string]::IsNullOrWhiteSpace($appName)) {
    Note "无法自动识别，请手动输入（就是创建应用时填的名称）"
    $appName = Read-Host "  应用名"
}
if ([string]::IsNullOrWhiteSpace($appName)) { Fail "应用名为空" }

$sandbox = "/apps/$appName"
Write-Host ""
Note "沙箱目录：$sandbox"

try {
    $listing = Api-List $sandbox
} catch {
    Fail ("列出目录失败：" + (Get-ErrorBody $_))
}

if ($listing.errno -ne 0) {
    Write-Host ""
    Say ("  沙箱目录还不存在（errno=" + $listing.errno + "），尝试自动创建 ...") Yellow
    $created = $false
    try {
        $mkUrl = "$PanBase/xpan/file?method=filemanager&opera=mkdir&access_token=$token"
        $mkBody = "filelist=" + [uri]::EscapeDataString('["' + $sandbox + '"]')
        $mkResp = Invoke-RestMethod -Uri $mkUrl -Method Post -Body $mkBody -ContentType "application/x-www-form-urlencoded" -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30
        if ($mkResp.errno -eq 0) { $created = $true } else { Note ("创建接口返回 errno=" + $mkResp.errno) }
    } catch {
        Note ("创建请求出错：" + (Get-ErrorBody $_))
    }

    if ($created) {
        Say "  [成功] 沙箱目录已创建" Green
        Start-Sleep -Seconds 2
        $listing = Api-List $sandbox
    } else {
        Write-Host ""
        Say "  自动创建没成功，请手动建（很简单）：" Yellow
        Write-Host "    1. 打开百度网盘客户端或网页版，进入「我的应用数据」"
        Write-Host ("    2. 点「新建文件夹」，名字必须完全是：" + $appName)
        Write-Host "    3. 建好后重新运行本脚本"
        Write-Host ""
        exit 0
    }
}

$files = @()
if ($listing.list -ne $null) {
    $files = @($listing.list | Where-Object { $_.isdir -eq 0 } | Sort-Object -Property size -Descending)
}

if ($files.Count -eq 0) {
    Write-Host ""
    Say "  沙箱目录是空的，没有东西可以测。" Yellow
    Write-Host ""
    Write-Host "  请这样操作：" White
    Write-Host "    1. 打开百度网盘客户端或网页版，进入「我的应用数据」"
    Write-Host ("    2. 找到文件夹 " + $appName + "（如果还没有，就新建一个，名字必须完全是这个）")
    Write-Host "    3. 进到该文件夹，上传一个大文件（建议 100MB 以上）"
    Write-Host "    4. 等上传完成后，重新运行本脚本"
    Write-Host ""
    exit 0
}

Write-Host ""
Say ("  目录里有 " + $files.Count + " 个文件，按体积从大到小：") Green
$show = [Math]::Min(15, $files.Count)
for ($i = 0; $i -lt $show; $i++) {
    $mb = [Math]::Round($files[$i].size / 1MB, 1)
    Write-Host ("    [" + $i + "] " + $files[$i].server_filename + "   " + $mb + " MB")
}
if ($files.Count -gt $show) { Note ("（其余 " + ($files.Count - $show) + " 个未显示）") }

Write-Host ""
Note "直接回车 = 测最大的那个（推荐）"
$sel = Read-Host "  选择编号"
$idx = 0
if (-not [string]::IsNullOrWhiteSpace($sel)) { $idx = [int]$sel }
if ($idx -lt 0 -or $idx -ge $files.Count) { $idx = 0 }
$target = $files[$idx]
$targetMb = [Math]::Round($target.size / 1MB, 1)
Say ("  已选中：" + $target.server_filename + "  (" + $targetMb + " MB)") Green

# ---------- 4. 取下载直链 ----------
Head "4 / 5  获取下载直链"
$fsid = $target.fs_id
$metaUrl = "$PanBase/xpan/multimedia?method=filemetas&fsids=%5B$fsid%5D&dlink=1&access_token=$token"
try {
    $meta = Invoke-RestMethod -Uri $metaUrl -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30
} catch {
    Fail ("获取直链失败：" + (Get-ErrorBody $_))
}
if ($meta.errno -ne 0) { Fail ("获取直链失败，errno=" + $meta.errno) }
if ($meta.list -eq $null -or $meta.list.Count -eq 0) { Fail "响应里没有文件信息" }

$dlink = $meta.list[0].dlink
if ([string]::IsNullOrWhiteSpace($dlink)) { Fail "没有拿到 dlink（可能需要文件在沙箱内，或该接口对该文件受限）" }
$dlink = $dlink -replace '\\u0026', '&'

$downloadUrl = $dlink + "&access_token=" + $token
Note "直链有效期 8 小时，本次只测速不保存文件"

# ---------- 5. 下载测速 ----------
Head ("5 / 5  开始测速（最多 " + $TestSecs + " 秒，只读不存盘）")
Write-Host ""

$req = [System.Net.HttpWebRequest]::Create($downloadUrl)
$req.UserAgent = $UserAgent
$req.AllowAutoRedirect = $true
$req.Timeout = 30000
$req.ReadWriteTimeout = 20000

$stream = $null
try {
    $resp = $req.GetResponse()
    $stream = $resp.GetResponseStream()
} catch {
    Fail ("下载请求失败：" + $_.Exception.Message + "`n         若提示 403，通常是缺少 User-Agent 或直链已失效")
}

$buf    = New-Object byte[] 262144
$total  = [long]0
$sw     = [System.Diagnostics.Stopwatch]::StartNew()
$lastTick = -1
$completed = $false

try {
    while ($sw.Elapsed.TotalSeconds -lt $TestSecs) {
        $n = $stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { $completed = $true; break }
        $total += $n

        $tick = [int]$sw.Elapsed.TotalSeconds
        if ($tick -ne $lastTick) {
            $lastTick = $tick
            $el = $sw.Elapsed.TotalSeconds
            if ($el -gt 0) {
                $cur = ($total / 1MB) / $el
                $line = "  已下载 {0,7:N1} MB    平均 {1,6:N2} MB/s    已用 {2,2}s" -f ($total / 1MB), $cur, $tick
                Write-Host $line -ForegroundColor DarkGray
            }
        }
    }
} finally {
    if ($stream -ne $null) { $stream.Close() }
}
$sw.Stop()

$elapsed = $sw.Elapsed.TotalSeconds
if ($elapsed -le 0) { $elapsed = 0.001 }
$avgBps  = $total / $elapsed
$avgKBs  = $avgBps / 1KB
$avgMBs  = $avgBps / 1MB

# ---------- 结果 ----------
Write-Host ""
Say "================ 测速结果 ================" White
Write-Host ("  文件        : " + $target.server_filename)
Write-Host ("  文件大小    : " + $targetMb + " MB")
Write-Host ("  已下载      : " + [Math]::Round($total / 1MB, 2) + " MB")
Write-Host ("  用时        : " + [Math]::Round($elapsed, 1) + " 秒")
if ($completed) { Note "（文件已下完，速度仍可参考）" }
Write-Host ""
Write-Host ("  平均速度    : " + [Math]::Round($avgMBs, 2) + " MB/s   (" + [Math]::Round($avgKBs, 0) + " KB/s)") White
Write-Host ""

$verdict = ""
$color = "Gray"
if     ($avgMBs -ge 5)   { $verdict = "优秀 . 这个通道很好用，下载取回基本无感";  $color = "Green" }
elseif ($avgMBs -ge 2)   { $verdict = "良好 . 通道可用，大文件会等一会儿";         $color = "Green" }
elseif ($avgMBs -ge 0.5) { $verdict = "偏慢 . 能用，但大文件取回要等，本地缓存要留久一点"; $color = "Yellow" }
elseif ($avgMBs -ge 0.1) { $verdict = "堪忧 . 取回通道基本不可用，只能当纯备份盘";   $color = "Red" }
else                     { $verdict = "不可用 . 建议放弃这个方案";                 $color = "Red" }

Say ("  判定：" + $verdict) $color
Write-Host ""
Note "对照参考："
Note "  - 若明显低于你客户端下载同一文件的速度，属正常（API 没有加速手段）"
Note "  - 本次只测了下载。上传速度将在开发联调时另测"
Write-Host ""

# ---------- 附加：多连接并发对比 ----------
# 单连接速度「稳定不变」往往意味着每连接限速，此时多连接并发可以叠加。
# 用 Windows 自带的 curl.exe 起 N 个进程，各下载文件的一段（Range 请求）。
$threads  = 8
$fileSize = [long]$target.size

Head ("附加测试：多连接并发（" + $threads + " 个连接）")

if ($fileSize -lt 8MB) {
    Note ("文件只有 " + [Math]::Round($fileSize / 1MB, 1) + " MB，太小，跳过并发对比")
} else {
    $hasCurl = $false
    try { $hasCurl = [bool](Get-Command "curl.exe" -ErrorAction SilentlyContinue) } catch { }

    if (-not $hasCurl) {
        Note "找不到 curl.exe，跳过并发对比（Windows 10 1803+ 自带）"
    } else {
        $tmpDir = Join-Path $env:TEMP ("baidu-mt-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
        Note "正在并发下载，约 10 秒 ..."
        Write-Host ""

        $chunk = [math]::Floor($fileSize / $threads)
        $procs = @()

        for ($i = 0; $i -lt $threads; $i++) {
            $rangeStart = $i * $chunk
            $rangeEnd = $fileSize - 1
            if ($i -lt ($threads - 1)) { $rangeEnd = ($i + 1) * $chunk - 1 }
            $outFile = Join-Path $tmpDir ("part-" + $i + ".bin")
            $cargs = @(
                "-s", "-L",
                "-o", $outFile,
                "-r", ($rangeStart.ToString() + "-" + $rangeEnd.ToString()),
                "-H", "User-Agent: pan.baidu.com",
                $downloadUrl
            )
            try {
                $procs += Start-Process -FilePath "curl.exe" -ArgumentList $cargs -PassThru -NoNewWindow
            } catch {
                Note ("第 " + $i + " 个连接启动失败")
            }
        }

        if ($procs.Count -eq 0) {
            Note "一个连接都没起来，跳过并发对比"
        } else {
            $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Seconds 10
            foreach ($pr in $procs) {
                try { if (-not $pr.HasExited) { $pr.Kill() } } catch { }
            }
            $sw2.Stop()
            Start-Sleep -Milliseconds 1000

            $mtTotal = [long]0
            try {
                foreach ($f in (Get-ChildItem -Path $tmpDir -File -ErrorAction SilentlyContinue)) {
                    $mtTotal += $f.Length
                }
            } catch { }

            $mtElapsed = $sw2.Elapsed.TotalSeconds
            if ($mtElapsed -le 0) { $mtElapsed = 0.001 }
            $mtMBs = ($mtTotal / 1MB) / $mtElapsed

            Write-Host ""
            Write-Host ("  并发已下载  : " + [Math]::Round($mtTotal / 1MB, 2) + " MB")
            Write-Host ("  并发用时    : " + [Math]::Round($mtElapsed, 1) + " 秒")
            Write-Host ("  并发平均    : " + [Math]::Round($mtMBs, 2) + " MB/s   (" + [Math]::Round($mtMBs * 1024, 0) + " KB/s)") White
            Write-Host ""

            $ratio = 0
            if ($avgMBs -gt 0) { $ratio = $mtMBs / $avgMBs }
            Write-Host ("  提升倍数    : " + [Math]::Round($ratio, 1) + " x") White
            Write-Host ""

            if ($ratio -ge 3) {
                Say "  结论：多连接明显有效，单连接限速可被叠加，取回通道有救" Green
            } elseif ($ratio -ge 1.8) {
                Say "  结论：多连接部分有效，能提速但不算质变" Yellow
            } else {
                Say "  结论：多连接无效，是账号级总带宽限制，取回通道确实没救" Red
            }
        }

        try { Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ""
Note "把「单连接」和「并发」两个数字都记下来，它们决定后面怎么设计。"
Write-Host ""
