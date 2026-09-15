# ============================================================
#  百度网盘开放平台 . 下载速度【对照诊断】
# ------------------------------------------------------------
#  目的：定位「慢」到底出在哪一层。同一个文件、同一条直链，
#        用三种不同方式下载，互相对比：
#
#    A. HttpWebRequest 单连接  —— 原测速脚本用的方式
#    B. curl 单连接            —— 换一个客户端实现，其余条件不变
#    C. curl 8 连接分段并发    —— 看单连接限速能否被叠加
#
#  判读：
#    A 慢 / B 快        -> 问题在脚本读取方式，不是服务端限速
#    A B 都慢 / C 快    -> 单连接被限速，但并发能救
#    A B C 都慢         -> 服务端确实限速，并发也救不了
#
#  凭据处理：AppKey / SecretKey / access_token 只在本次运行的内存里使用，
#            不写入任何文件，也不打印到屏幕。
#
#  用法：双击同目录下的 baidu-diag.bat
# ============================================================

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$OAuthBase = "https://openapi.baidu.com/oauth/2.0"
$PanBase   = "https://pan.baidu.com/rest/2.0"
$UserAgent = "pan.baidu.com"
$TestSecs  = 10
$Threads   = 8

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
function Get-ErrorBody($err) {
    try {
        $resp = $err.Exception.Response
        if ($resp -ne $null) {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            return $reader.ReadToEnd()
        }
    } catch { }
    return ""
}

Write-Host ""
Say "============================================" White
Say "  百度网盘开放平台 . 下载速度对照诊断" White
Say "============================================" White
Note "凭据只在本次运行中使用，不写入任何文件"

# ---------- 1. 凭据 ----------
function Read-CredentialFromClipboard($label, $expectLen) {
    while ($true) {
        Write-Host ("  请复制【" + $label + "】到剪贴板（在网页上选中后 Ctrl+C），然后按回车")
        Read-Host "  " | Out-Null

        $v = ""
        try {
            $v = Get-Clipboard -Raw
        } catch {
            Write-Host "  [!] 读剪贴板失败，改为手动粘贴（屏幕上会显示出来，这是正常的）" -ForegroundColor Yellow
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
        $head4 = $v.Substring(0, [Math]::Min(4, $v.Length))
        Write-Host ("  [√] " + $label + " 已读取：" + $head4 + "****（共 " + $v.Length + " 位）") -ForegroundColor Green
        return $v
    }
}

Head "1 / 6  输入应用凭据"
Note "在 https://pan.baidu.com/union/console/applist 点开你的应用即可看到这两个值"
Write-Host ""
$appKey    = Read-CredentialFromClipboard "AppKey" 32
$appSecret = Read-CredentialFromClipboard "SecretKey" 32

# ---------- 2. 授权 ----------
Head "2 / 6  授权（浏览器里点同意）"
$authUrl = "$OAuthBase/authorize?response_type=code&client_id=$appKey&redirect_uri=oob&scope=basic,netdisk"
Note "即将打开浏览器，登录百度账号后点「同意授权」，页面会显示授权码，复制它"
Write-Host ""
Read-Host "  准备好后按回车打开浏览器"
Start-Process $authUrl | Out-Null
Write-Host ""
$code = (Read-Host "  粘贴授权码").Trim()
if ([string]::IsNullOrWhiteSpace($code)) { Fail "授权码为空" }

Write-Host ""
Note "正在换取 access_token ..."
$tokenUrl = "$OAuthBase/token?grant_type=authorization_code&code=$code&client_id=$appKey&client_secret=$appSecret&redirect_uri=oob"
try {
    $tok = Invoke-RestMethod -Uri $tokenUrl -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30
} catch {
    Fail ("换取 token 失败。" + (Get-ErrorBody $_) + "`n         常见原因：授权码已用过（只能用一次，10 分钟过期），或 AppKey 与 SecretKey 不匹配")
}
if ([string]::IsNullOrWhiteSpace($tok.access_token)) { Fail ("响应里没有 access_token：" + ($tok | ConvertTo-Json -Compress)) }
$token = $tok.access_token
Write-Host ""
Say "  [成功] 已获得 access_token" Green

function Api-List($dir) {
    $enc = (($dir -split "/") | ForEach-Object { [uri]::EscapeDataString($_) }) -join "/"
    $url = "$PanBase/xpan/file?method=list&access_token=$token&dir=$enc&order=size&desc=1&limit=1000"
    return Invoke-RestMethod -Uri $url -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30
}

# ---------- 3. 定位沙箱目录 ----------
Head "3 / 6  定位应用沙箱目录"
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
Note "沙箱目录：$sandbox"

try { $listing = Api-List $sandbox } catch { Fail ("列出目录失败：" + (Get-ErrorBody $_)) }

if ($listing.errno -ne 0) {
    Write-Host ""
    Say ("  沙箱目录还不存在（errno=" + $listing.errno + "），尝试自动创建 ...") Yellow
    $created = $false
    try {
        $mkUrl  = "$PanBase/xpan/file?method=filemanager&opera=mkdir&access_token=$token"
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
        Say "  自动创建没成功，请手动建：" Yellow
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
    Write-Host ("    2. 找到文件夹 " + $appName + "（没有就新建一个，名字必须完全是这个）")
    Write-Host "    3. 进到该文件夹，上传文件（建议同时放一个几 MB 的小文件和一个 100MB 的大文件）"
    Write-Host "    4. 等上传完成后，重新运行本脚本"
    Write-Host ""
    exit 0
}

# ---------- 4. 选文件 ----------
Head "4 / 6  选择要测的文件"
Say ("  目录里有 " + $files.Count + " 个文件，按体积从大到小：") Green
$show = [Math]::Min(15, $files.Count)
for ($i = 0; $i -lt $show; $i++) {
    $mb = [Math]::Round($files[$i].size / 1MB, 2)
    Write-Host ("    [" + $i + "] " + $files[$i].server_filename + "   " + $mb + " MB")
}
Write-Host ""
Note "提示：分别测「小文件」和「大文件」，能看出限速是否与文件大小有关"
Note "直接回车 = 测最大的那个"
$sel = Read-Host "  选择编号"
$idx = 0
if (-not [string]::IsNullOrWhiteSpace($sel)) { $idx = [int]$sel }
if ($idx -lt 0 -or $idx -ge $files.Count) { $idx = 0 }
$target   = $files[$idx]
$targetMb = [Math]::Round($target.size / 1MB, 2)
$fileSize = [long]$target.size
Say ("  已选中：" + $target.server_filename + "  (" + $targetMb + " MB)") Green

# ---------- 5. 取直链 ----------
Head "5 / 6  获取下载直链"
$fsid    = $target.fs_id
$metaUrl = "$PanBase/xpan/multimedia?method=filemetas&fsids=%5B$fsid%5D&dlink=1&access_token=$token"
try { $meta = Invoke-RestMethod -Uri $metaUrl -Headers @{ "User-Agent" = $UserAgent } -TimeoutSec 30 } catch { Fail ("获取直链失败：" + (Get-ErrorBody $_)) }
if ($meta.errno -ne 0) { Fail ("获取直链失败，errno=" + $meta.errno) }
if ($meta.list -eq $null -or $meta.list.Count -eq 0) { Fail "响应里没有文件信息" }
$dlink = $meta.list[0].dlink
if ([string]::IsNullOrWhiteSpace($dlink)) { Fail "没有拿到 dlink" }
$dlink = $dlink -replace '\\u0026', '&'
$downloadUrl = $dlink + "&access_token=" + $token
Note "直链有效期 8 小时，本次只测速不保存文件"

# 代理诊断：若系统配了代理，HttpWebRequest 会走代理，可能拖慢速度
$proxyTxt = "无"
try {
    $dp = [System.Net.WebRequest]::DefaultWebProxy
    if ($dp -ne $null) {
        $pu = $dp.GetProxy([uri]$downloadUrl)
        if ($pu -ne $null) { $proxyTxt = $pu.ToString() }
    }
} catch { }
Note ("系统代理（诊断用）：" + $proxyTxt)

# 把「纯直链」放剪贴板：不含 access_token，8 小时失效，可贴给虾哥在本机复测
try {
    Set-Clipboard -Value $dlink
    Write-Host ""
    Say "  [已复制] 纯直链已放进剪贴板（不含密钥，8 小时后失效）" Green
    Note "如果这次测速仍不理想，直接粘贴发给我，我在本机用别的方式复测"
} catch { }
Write-Host ""

# ---------- 6. 三方式对照 ----------
Head "6 / 6  三方式对照测试"

$hasCurl = $false
try { $hasCurl = [bool](Get-Command "curl.exe" -ErrorAction SilentlyContinue) } catch { }
if (-not $hasCurl) { Fail "找不到 curl.exe（Windows 10 1803+ 自带），无法做对照测试" }

$tmpDir = Join-Path $env:TEMP ("baidu-diag-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

# ---- 方式 A：HttpWebRequest 单连接（原测速脚本用的方式） ----
Write-Host ""
Note ("方式 A：HttpWebRequest 单连接，测 " + $TestSecs + " 秒 ...")
$mbsA = 0
try {
    $req = [System.Net.HttpWebRequest]::Create($downloadUrl)
    $req.UserAgent = $UserAgent
    $req.Proxy = $null
    $req.AllowAutoRedirect = $true
    $req.Timeout = 30000
    $req.ReadWriteTimeout = 30000
    $stream = $req.GetResponse().GetResponseStream()

    $buf   = New-Object byte[] 1048576
    $totA  = [long]0
    $swA   = [System.Diagnostics.Stopwatch]::StartNew()
    $tickA = -1
    while ($swA.Elapsed.TotalSeconds -lt $TestSecs) {
        $n = $stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        $totA += $n
        $nowTick = [int]$swA.Elapsed.TotalSeconds
        if ($nowTick -ne $tickA) {
            $tickA = $nowTick
            $el = $swA.Elapsed.TotalSeconds
            if ($el -gt 0) {
                Write-Host ("    A: {0,7:N2} MB   {1,6:N2} MB/s   {2,2}s" -f ($totA / 1MB), (($totA / 1MB) / $el), $nowTick) -ForegroundColor DarkGray
            }
        }
    }
    $swA.Stop()
    $stream.Close()
    $elA = $swA.Elapsed.TotalSeconds
    if ($elA -le 0) { $elA = 0.001 }
    $mbsA = ($totA / 1MB) / $elA
} catch {
    Note ("方式 A 出错：" + $_.Exception.Message)
}

function Run-CurlSingle($url, $outFile, $secs) {
    try {
        $p = Start-Process -FilePath "curl.exe" -ArgumentList @("-sS", "-L", "-o", $outFile, "-A", "pan.baidu.com", $url) -PassThru -NoNewWindow
    } catch { return 0 }
    if ($p -eq $null) { return 0 }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $secs
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    $sw.Stop()
    Start-Sleep -Milliseconds 800
    $bytes = 0
    if (Test-Path $outFile) { $bytes = (Get-Item $outFile).Length }
    $el = $sw.Elapsed.TotalSeconds
    if ($el -le 0) { $el = 0.001 }
    return (($bytes / 1MB) / $el)
}

# ---- 方式 B：curl 单连接 ----
Write-Host ""
Note ("方式 B：curl 单连接，测 " + $TestSecs + " 秒 ...")
$mbsB = Run-CurlSingle $downloadUrl (Join-Path $tmpDir "single.bin") $TestSecs
Write-Host ("    B: " + [Math]::Round($mbsB, 2) + " MB/s") -ForegroundColor DarkGray

# ---- 方式 C：curl 分段并发 ----
Write-Host ""
$mbsC = 0
if ($fileSize -lt (8 * 1024 * 1024)) {
    Note ("方式 C：文件只有 " + [Math]::Round($fileSize / 1MB, 2) + " MB，太小，跳过并发测试")
} else {
    Note ("方式 C：curl " + $Threads + " 连接分段并发，测 " + $TestSecs + " 秒 ...")
    $chunk = [math]::Floor($fileSize / $Threads)
    $procs = @()
    for ($i = 0; $i -lt $Threads; $i++) {
        $rs = $i * $chunk
        $re = $fileSize - 1
        if ($i -lt ($Threads - 1)) { $re = ($i + 1) * $chunk - 1 }
        $outF  = Join-Path $tmpDir ("part-" + $i + ".bin")
        $cargs = @("-sS", "-L", "-o", $outF, "-r", ($rs.ToString() + "-" + $re.ToString()), "-A", "pan.baidu.com", $downloadUrl)
        try { $procs += Start-Process -FilePath "curl.exe" -ArgumentList $cargs -PassThru -NoNewWindow } catch { }
    }
    if ($procs.Count -eq 0) {
        Note "一个连接都没起来，跳过"
    } else {
        $swC = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $TestSecs
        foreach ($pr in $procs) { try { if (-not $pr.HasExited) { $pr.Kill() } } catch { } }
        $swC.Stop()
        Start-Sleep -Milliseconds 1000
        $totC = [long]0
        try {
            foreach ($f in (Get-ChildItem -Path $tmpDir -File -ErrorAction SilentlyContinue)) {
                if ($f.Name -like "part-*") { $totC += $f.Length }
            }
        } catch { }
        $elC = $swC.Elapsed.TotalSeconds
        if ($elC -le 0) { $elC = 0.001 }
        $mbsC = ($totC / 1MB) / $elC
        Write-Host ("    C: " + [Math]::Round($mbsC, 2) + " MB/s") -ForegroundColor DarkGray
    }
}

try { Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }

# ---------- 结论 ----------
Write-Host ""
Say "============ 对照结果 ============" White
Write-Host ("  被测文件 : " + $target.server_filename + "  (" + $targetMb + " MB)")
Write-Host ""
Write-Host ("  A. HttpWebRequest 单连接 : " + [Math]::Round($mbsA, 2) + " MB/s   (" + [Math]::Round($mbsA * 1024, 0) + " KB/s)")
Write-Host ("  B. curl 单连接           : " + [Math]::Round($mbsB, 2) + " MB/s   (" + [Math]::Round($mbsB * 1024, 0) + " KB/s)")
Write-Host ("  C. curl " + $Threads + " 连接并发        : " + [Math]::Round($mbsC, 2) + " MB/s   (" + [Math]::Round($mbsC * 1024, 0) + " KB/s)")
Write-Host ""

if ($mbsA -gt 0 -and $mbsB -gt ($mbsA * 1.8)) {
    Say "  >> A 明显慢于 B：问题在脚本的读取方式，不是服务端限速" Red
} elseif ($mbsB -gt 0 -and $mbsC -gt ($mbsB * 1.8)) {
    Say "  >> C 明显快于 B：单连接被限速，但并发可以叠加，取回通道有救" Green
} elseif ($mbsA -lt 0.2 -and $mbsB -lt 0.2 -and $mbsC -lt 0.3) {
    Say "  >> 三种方式都慢：服务端对这个文件确实限速，并发也救不了" Red
} else {
    Say "  >> 请把上面六个数字发给我（A/B/C 的 MB/s 与 KB/s）" Yellow
}

Write-Host ""
Note "建议再用一个几 MB 的小文件跑一遍，对比不同体积下的速度差异。"
Write-Host ""
