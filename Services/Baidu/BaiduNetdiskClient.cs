using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;

namespace FocusCapture.Services.Baidu;

/// <summary>百度开放平台业务错误。ErrNo 来自官方文档定义，不是猜测。</summary>
public sealed class BaiduApiException : Exception
{
    public int ErrNo { get; }

    public BaiduApiException(int errNo, string message) : base(message) => ErrNo = errNo;

    /// <summary>授权类错误（需要重新走设备码授权或先刷新令牌）。</summary>
    public bool IsAuthError => ErrNo is -6 or 111 or 112;

    /// <summary>频率/配额类错误（可延时重试）。</summary>
    public bool IsThrottled => ErrNo is 20012 or 31034;
}

/// <summary>尚未授权（本机没有可用令牌）。</summary>
public sealed class BaiduAuthRequiredException : Exception
{
    public BaiduAuthRequiredException(string message) : base(message) { }
}

/// <summary>沙箱内单个条目。</summary>
public sealed class BaiduFileEntry
{
    public long FsId { get; init; }
    /// <summary>沙箱内完整路径，形如 /apps/FocusCapture/files/xxx.md</summary>
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool IsDir { get; init; }
    public string Md5 { get; init; } = "";
    public DateTime ModifiedAt { get; init; }
}

/// <summary>设备码授权第一步的返回。</summary>
public sealed class BaiduDeviceCode
{
    public string DeviceCode { get; init; } = "";
    public string UserCode { get; init; } = "";
    public string VerificationUrl { get; init; } = "";
    public string QrCodeUrl { get; init; } = "";
    public int ExpiresInSeconds { get; init; } = 300;
    public int IntervalSeconds { get; init; } = 5;
}

/// <summary>
/// 百度网盘开放平台客户端（2026-09-16）。
///
/// 授权走**设备码模式**（官方推荐路径，见调研报告 2.2）：不需要回调服务器，适合桌面应用。
///
/// 三条硬约束（都来自官方文档，改动前先读调研报告）：
/// 1. <b>只能碰 /apps/{应用名} 沙箱</b> —— 2026-06-03 后创建的应用一律如此，不是配置项。所有路径都过
///    <see cref="EnsureSandboxed"/> 校验，越界直接拒绝（既防手滑，也防将来谁把 AI 给的路径直接传进来）。
/// 2. <b>User-Agent 必须是 pan.baidu.com</b> —— 否则下载直链返回 31326 防盗链错误。
/// 3. <b>refresh_token 一次性</b> —— 每次刷新后旧的立即作废，刷新成功必须马上落盘（见 BaiduCredentialStore）。
///
/// 错误码只做「翻译成人话」，不拿它推断业务状态（夸克那次的教训：错误码不是真相来源）。
/// </summary>
public class BaiduNetdiskClient
{
    private const string OAuthDeviceCodeUrl = "https://openapi.baidu.com/oauth/2.0/device/code";
    private const string OAuthTokenUrl = "https://openapi.baidu.com/oauth/2.0/token";
    private const string XpanFileUrl = "https://pan.baidu.com/rest/2.0/xpan/file";
    private const string XpanMultimediaUrl = "https://pan.baidu.com/rest/2.0/xpan/multimedia";
    private const string SuperFile2Url = "https://pan.baidu.com/rest/2.0/pcs/superfile2";

    /// <summary>第三方应用唯一能访问的顶层目录。</summary>
    public const string SandboxPrefix = "/apps";

    /// <summary>分片大小：官方要求普通用户 4MB，且首片必须 4MB、总片数 ≤1024。</summary>
    public const int SliceBytes = 4 * 1024 * 1024;

    /// <summary>单文件上限（普通用户 4G；会员/超会更高，超限由服务端 errno 拦下并给提示）。</summary>
    public const long MaxSingleFileBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>令牌刷新闸：refresh_token 一次性，并发刷新会让两边都作废，必须串行。</summary>
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private readonly BaiduCredentials _creds;

    /// <summary>沙箱内的工作目录，形如 /apps/FocusCapture/files（由设置面板配置，默认见 DefaultNetRoot）。</summary>
    public string NetRoot { get; }

    public const string DefaultNetRoot = "/apps/FocusCapture";

    public BaiduNetdiskClient(BaiduCredentials credentials, string netRoot = DefaultNetRoot)
    {
        _creds = credentials;
        NetRoot = NormalizeNetRoot(netRoot);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // 所有 pan.baidu.com 请求都要带这个 UA（下载 dlink 不带会吃 31326 防盗链）
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pan.baidu.com");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    // ══════════════════ 授权 ══════════════════

    /// <summary>第一步：取设备码与用户码（用户拿去扫码/输码）。</summary>
    public static async Task<BaiduDeviceCode> RequestDeviceCodeAsync(string appKey, CancellationToken ct)
    {
        var url = $"{OAuthDeviceCodeUrl}?response_type=device_code&client_id={Uri.EscapeDataString(appKey)}" +
                  "&scope=basic,netdisk";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var code = new BaiduDeviceCode
        {
            DeviceCode = Str(json, "device_code"),
            UserCode = Str(json, "user_code"),
            VerificationUrl = Str(json, "verification_url"),
            QrCodeUrl = Str(json, "qrcode_url"),
            ExpiresInSeconds = IntOr(json, "expires_in", 300),
            IntervalSeconds = Math.Max(5, IntOr(json, "interval", 5)),   // 官方要求轮询间隔 ≥5 秒
        };

        if (code.DeviceCode.Length == 0)
            throw new BaiduApiException(Str(json, "error") == "invalid_client" ? -6 : 2,
                $"获取授权码失败：{Str(json, "error_description")}".TrimEnd('：'));
        return code;
    }

    /// <summary>
    /// 第二步：按 interval 轮询换令牌，直到用户授权 / 超时 / 取消。
    /// 官方错误语义：authorization_pending = 用户还没操作（继续等）；其余 = 终止。
    /// </summary>
    public static async Task<BaiduToken> PollDeviceTokenAsync(
        string appKey, string secretKey, BaiduDeviceCode code, Action<string>? status, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddSeconds(code.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(code.IntervalSeconds);

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct).ConfigureAwait(false);

            var url = $"{OAuthTokenUrl}?grant_type=device_token&code={Uri.EscapeDataString(code.DeviceCode)}" +
                      $"&client_id={Uri.EscapeDataString(appKey)}&client_secret={Uri.EscapeDataString(secretKey)}";
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

            var access = Str(json, "access_token");
            if (access.Length > 0)
            {
                status?.Invoke("授权成功");
                return BuildToken(json);
            }

            var error = Str(json, "error");
            if (error is "authorization_pending" or "")
            {
                status?.Invoke($"等待授权…（剩 {Math.Max(0, (int)(deadline - DateTime.Now).TotalSeconds)} 秒）");
                continue;
            }

            throw error switch
            {
                "authorization_declined" => new BaiduApiException(111, "用户在授权页取消了授权。"),
                "expired_token" => new BaiduApiException(112, "授权码已过期，请重新发起授权。"),
                "invalid_client" => new BaiduApiException(-6, "AppKey 或 SecretKey 不正确，请核对设置里的应用凭据。"),
                _ => new BaiduApiException(2, $"授权失败：{Str(json, "error_description")}".TrimEnd('：')),
            };
        }

        throw new BaiduApiException(112, "授权超时（未在有效期内完成扫码/输码），请重新发起。");
    }

    /// <summary>
    /// 刷新令牌。**成功后立刻落盘**（旧 refresh_token 当场作废）。
    /// 串行化：并发刷新会导致两边都失效，只能让用户重新授权。
    /// </summary>
    public async Task<BaiduToken> RefreshTokenAsync(CancellationToken ct)
    {
        await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = BaiduCredentialStore.LoadToken();
            if (current == null || current.RefreshToken.Length == 0)
                throw new BaiduAuthRequiredException("尚无授权记录，请先在设置中完成百度网盘授权。");

            var url = $"{OAuthTokenUrl}?grant_type=refresh_token&refresh_token={Uri.EscapeDataString(current.RefreshToken)}" +
                      $"&client_id={Uri.EscapeDataString(_creds.AppKey)}&client_secret={Uri.EscapeDataString(_creds.SecretKey)}";
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (Str(json, "access_token").Length == 0)
            {
                // refresh_token 一次性：这里失败基本等于必须重新授权，给明确指引而不是静默卡死
                throw new BaiduApiException(-6,
                    $"刷新授权失败（{Str(json, "error_description")}）。refresh_token 只能使用一次，" +
                    "请到「设置 → 文件与网盘」重新授权。");
            }

            var token = BuildToken(json);
            if (token.RefreshToken.Length == 0)
                token.RefreshToken = current.RefreshToken;   // 极少数情况下服务端不回新 refresh_token，保留旧的
            BaiduCredentialStore.SaveToken(token);
            AppLog.Info("Baidu", "授权令牌已刷新并落盘");
            return token;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    /// <summary>取可用令牌；临近到期（<2 天）先主动刷新，避免用到一半失效。</summary>
    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
    {
        var token = BaiduCredentialStore.LoadToken()
                    ?? throw new BaiduAuthRequiredException("尚未授权百度网盘，请到「设置 → 文件与网盘」完成授权。");

        if (!token.IsValid)
        {
            if (token.RefreshToken.Length == 0)
                throw new BaiduAuthRequiredException("百度网盘授权已过期，请重新授权。");
            token = await RefreshTokenAsync(ct).ConfigureAwait(false);
        }
        else if (token.ExpiresAt - DateTime.Now < TimeSpan.FromDays(2))
        {
            try { token = await RefreshTokenAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Baidu", "提前刷新令牌失败，继续用当前令牌：" + ex.Message); }
        }
        return token.AccessToken;
    }

    private static BaiduToken BuildToken(JsonElement json) => new()
    {
        AccessToken = Str(json, "access_token"),
        RefreshToken = Str(json, "refresh_token"),
        // 官方时效 30 天；预留 1 小时缓冲，避免踩在边界上失效
        ExpiresAt = DateTime.Now.AddSeconds(Math.Max(3600, IntOr(json, "expires_in", 2592000)) - 3600),
    };

    // ══════════════════ 文件：列目录 / 建目录 / 查 / 删除 ══════════════════

    /// <summary>列出沙箱内某目录下的条目（只返回直属子项）。</summary>
    public async Task<List<BaiduFileEntry>> ListAsync(string netDir, CancellationToken ct)
    {
        // 列目录是只读，放行 /apps 本身（否则「列应用目录」这种正常请求会被自己的守卫拦下）
        EnsureSandboxed(netDir, allowSandboxRoot: true);
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        var url = $"{XpanFileUrl}?method=list&access_token={Uri.EscapeDataString(token)}" +
                  $"&dir={Uri.EscapeDataString(netDir)}&order=time&desc=1&limit=1000";
        var json = await GetJsonAsync(url, ct, treatMissingAsEmpty: true).ConfigureAwait(false);

        var errno = IntOr(json, "errno", 0);
        // 目录不存在：官方返回 -9；沙箱目录尚未创建时这是常态，按"空目录"处理
        if (errno is -9 or 31066) return new List<BaiduFileEntry>();
        if (errno != 0) throw new BaiduApiException(errno, DescribeErrNo(errno));

        var list = new List<BaiduFileEntry>();
        if (json.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var path = Str(item, "path");
                if (path.Length == 0) continue;
                list.Add(new BaiduFileEntry
                {
                    FsId = LongOr(item, "fs_id"),
                    Path = path,
                    Name = Str(item, "server_filename"),
                    Size = LongOr(item, "size"),
                    IsDir = IntOr(item, "isdir") == 1,
                    Md5 = Str(item, "md5"),
                    ModifiedAt = FromUnix(IntOr(item, "server_mtime")),
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 按名字在父目录里找条目（不缓存 fsid —— 官方文档未承诺 fsid 跨会话稳定，现场查最稳）。
    ///
    /// <b>用途限定</b>：给「已知完整路径、要拿它的 fsid / size / md5」的场景用。
    /// **不要拿它探测目录是否存在** —— 那会列一次父目录，而父目录很可能就是 `/apps`，
    /// 第三方应用能不能列 `/apps` 官方从未承诺。目录一律交给 <see cref="EnsureDirectoryAsync"/>。
    /// </summary>
    public async Task<BaiduFileEntry?> FindAsync(string netPath, CancellationToken ct)
    {
        var parent = ParentOf(netPath);
        var name = netPath[(netPath.LastIndexOf('/') + 1)..];
        var entries = await ListAsync(parent, ct).ConfigureAwait(false);
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }

    /// <summary>本进程内已确保过的目录（建目录幂等，重复建纯属浪费请求与频控额度）。</summary>
    private static readonly HashSet<string> EnsuredDirs = new(StringComparer.Ordinal);
    private static readonly object EnsuredDirsGate = new();

    /// <summary>
    /// 逐级确保目录存在（/apps 已由平台保证存在，从它的下一级开始建）。
    ///
    /// <b>实现要点（2026-09-16 改）</b>：直接 create，把 -8「已存在」当成功 ——
    /// 不再先 list 父目录探测。原因：探测「/apps/FocusCapture 在不在」必须列 /apps，
    /// 而第三方应用能不能列 /apps 官方从未承诺。一旦列不动，整条上传链路就死在这一步
    /// （当时现象：测试连接报错、每个文件 78 毫秒内失败 5 次、网盘永远空）。
    /// 少一次请求、少一个外部依赖，行为还更确定。
    /// </summary>
    public async Task EnsureDirectoryAsync(string netDir, CancellationToken ct)
    {
        EnsureSandboxed(netDir);
        var parts = netDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (current == SandboxPrefix) continue;   // /apps 平台自带，不能也不需要创建

            lock (EnsuredDirsGate)
            {
                if (EnsuredDirs.Contains(current)) continue;
            }

            await CreateDirectoryAsync(current, ct).ConfigureAwait(false);

            lock (EnsuredDirsGate) EnsuredDirs.Add(current);
        }
    }

    private async Task CreateDirectoryAsync(string netDir, CancellationToken ct)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string> { ["path"] = netDir, ["isdir"] = "1", ["size"] = "0" };
        var json = await PostFormAsync($"{XpanFileUrl}?method=create&access_token={Uri.EscapeDataString(token)}", form, ct)
            .ConfigureAwait(false);
        var errno = IntOr(json, "errno", 0);
        // -8 = 已存在：并发创建下的正常结果，不算失败
        if (errno != 0 && errno != -8)
            throw new BaiduApiException(errno, $"创建目录失败：{DescribeErrNo(errno)}");
    }

    /// <summary>删除（沙箱内，可批量）。走官方 filemanager 的 delete 动作。</summary>
    public async Task DeleteAsync(IEnumerable<string> netPaths, CancellationToken ct)
    {
        var paths = netPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p =>
        {
            EnsureSandboxed(p);
            return p;
        }).ToList();
        if (paths.Count == 0) return;

        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["opera"] = "delete",
            ["async"] = "0",
            ["filelist"] = JsonSerializer.Serialize(paths, BaiduJsonContext.Default.ListString),
        };
        var json = await PostFormAsync($"{XpanFileUrl}?method=filemanager&access_token={Uri.EscapeDataString(token)}", form, ct)
            .ConfigureAwait(false);
        var errno = IntOr(json, "errno", 0);
        if (errno != 0) throw new BaiduApiException(errno, $"删除失败：{DescribeErrNo(errno)}");
    }

    /// <summary>重命名（沙箱内）。</summary>
    public async Task RenameAsync(string netPath, string newName, CancellationToken ct)
    {
        EnsureSandboxed(netPath);
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["opera"] = "rename",
            ["async"] = "0",
            ["filelist"] = JsonSerializer.Serialize(
                new List<BaiduRenameItem> { new() { Path = netPath, NewName = newName } },
                BaiduJsonContext.Default.ListBaiduRenameItem),
        };
        var json = await PostFormAsync($"{XpanFileUrl}?method=filemanager&access_token={Uri.EscapeDataString(token)}", form, ct)
            .ConfigureAwait(false);
        var errno = IntOr(json, "errno", 0);
        if (errno != 0) throw new BaiduApiException(errno, $"重命名失败：{DescribeErrNo(errno)}");
    }

    // ══════════════════ 上传（三阶段 + 分片） ══════════════════

    /// <summary>
    /// 上传一个本地文件到沙箱内。返回云端路径。
    /// 三阶段：precreate（登记 + 秒传判定）→ superfile2（逐片上传）→ create（提交）。
    /// </summary>
    public async Task<string> UploadFileAsync(string localPath, string netPath, IProgress<double>? progress, CancellationToken ct)
    {
        EnsureSandboxed(netPath);
        var info = new FileInfo(localPath);
        if (!info.Exists) throw new FileNotFoundException("本地文件不存在", localPath);
        if (info.Length > MaxSingleFileBytes)
            throw new BaiduApiException(-11,
                $"文件 {info.Name} 有 {RootMigrationService.FormatSize(info.Length)}，" +
                "超过普通用户 4GB 的单文件上限，请先压缩或升级网盘会员。");

        // 空文件没有分片：官方要求 block_list 至少一项，用空内容的 md5 占位
        var data = info.Length == 0 ? Array.Empty<byte>() : null;
        var blockList = new List<string>();
        if (info.Length == 0)
        {
            blockList.Add(Md5Hex(Array.Empty<byte>()));
        }
        else
        {
            using var fs = File.OpenRead(localPath);
            var buffer = new byte[SliceBytes];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (read == buffer.Length) blockList.Add(Md5Hex(buffer));
                else
                {
                    var tail = new byte[read];
                    Array.Copy(buffer, tail, read);
                    blockList.Add(Md5Hex(tail));
                }
            }
        }

        // ① precreate
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var preForm = new Dictionary<string, string>
        {
            ["path"] = netPath,
            ["size"] = info.Length.ToString(),
            ["isdir"] = "0",
            ["autoinit"] = "1",
            ["rtype"] = "3",
            ["block_list"] = JsonSerializer.Serialize(blockList, BaiduJsonContext.Default.ListString),
        };
        var pre = await PostFormAsync($"{XpanFileUrl}?method=precreate&access_token={Uri.EscapeDataString(token)}", preForm, ct)
            .ConfigureAwait(false);
        var preErrno = IntOr(pre, "errno", 0);
        if (preErrno != 0) throw new BaiduApiException(preErrno, $"上传登记失败：{DescribeErrNo(preErrno)}");

        var uploadId = Str(pre, "uploadid");
        var returnType = IntOr(pre, "return_type");
        var serverBlocks = new List<string>();
        if (pre.TryGetProperty("block_list", out var bl) && bl.ValueKind == JsonValueKind.Array)
            foreach (var b in bl.EnumerateArray()) serverBlocks.Add(b.GetString() ?? "");

        // return_type = 1 → 秒传：内容已在网盘里，一个字节都不用传
        var rapid = returnType == 1;
        if (!rapid)
        {
            if (uploadId.Length == 0)
                throw new BaiduApiException(2, "上传登记未返回 uploadid，无法继续。");

            using var fs = File.OpenRead(localPath);
            long sent = 0;
            for (var seq = 0; seq < blockList.Count; seq++)
            {
                ct.ThrowIfCancellationRequested();

                var size = (int)Math.Min(SliceBytes, info.Length - (long)seq * SliceBytes);
                var slice = new byte[size];
                if (size > 0) fs.ReadExactly(slice, 0, size);

                // precreate 回来的 block_list 里已经有值的片，说明服务端已存过（断点续传）→ 跳过上传。
                // 这是官方支持的省流量路径，重试一次中断的上传时能省掉绝大部分传输。
                if (seq < serverBlocks.Count && !string.IsNullOrEmpty(serverBlocks[seq]))
                    AppLog.Info("Baidu", $"分片 {seq + 1} 已在服务端，跳过上传");
                else
                    await UploadSliceAsync(token, netPath, uploadId, seq, slice, ct).ConfigureAwait(false);

                sent += size;
                if (info.Length > 0) progress?.Report((double)sent / info.Length);
            }
        }
        else
        {
            progress?.Report(1.0);
            AppLog.Info("Baidu", $"秒传命中（内容已存在）：{Path.GetFileName(netPath)}");
        }

        // ③ create 提交
        var createForm = new Dictionary<string, string>
        {
            ["path"] = netPath,
            ["size"] = info.Length.ToString(),
            ["isdir"] = "0",
            ["uploadid"] = uploadId,
            ["block_list"] = JsonSerializer.Serialize(blockList, BaiduJsonContext.Default.ListString),
        };
        var create = await PostFormAsync($"{XpanFileUrl}?method=create&access_token={Uri.EscapeDataString(token)}", createForm, ct)
            .ConfigureAwait(false);
        var createErrno = IntOr(create, "errno", 0);
        if (createErrno != 0) throw new BaiduApiException(createErrno, $"上传提交失败：{DescribeErrNo(createErrno)}");

        progress?.Report(1.0);
        return netPath;
    }

    /// <summary>上传单分片：superfile2 的 access_token 在 query 上，文件走 multipart body。</summary>
    private async Task UploadSliceAsync(string token, string netPath, string uploadId, int partSeq, byte[] slice, CancellationToken ct)
    {
        var url = $"{SuperFile2Url}?method=upload&access_token={Uri.EscapeDataString(token)}" +
                  "&type=tmpfile" +
                  $"&path={Uri.EscapeDataString(netPath)}" +
                  $"&uploadid={Uri.EscapeDataString(uploadId)}" +
                  $"&partseq={partSeq}";

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(slice);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "slice");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        finally
        {
            content.Dispose();
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = TryParse(body);
            if (doc == null)
                throw new BaiduApiException(2, $"分片 {partSeq + 1} 上传返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");

            var errno = IntOr(doc.RootElement, "errno", 0);
            if (errno != 0)
                throw new BaiduApiException(errno, $"分片 {partSeq + 1} 上传失败：{DescribeErrNo(errno)}");
        }
    }

    // ══════════════════ 下载 ══════════════════

    /// <summary>取下载直链（8 小时有效，且必须拼 access_token + 指定 UA）。</summary>
    public async Task<string?> GetDownloadLinkAsync(string netPath, CancellationToken ct)
    {
        var entry = await FindAsync(netPath, ct).ConfigureAwait(false);
        if (entry == null) return null;
        return await GetDownloadLinkByFsIdAsync(entry.FsId, ct).ConfigureAwait(false);
    }

    private async Task<string?> GetDownloadLinkByFsIdAsync(long fsId, CancellationToken ct)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        var url = $"{XpanMultimediaUrl}?method=filemetas&access_token={Uri.EscapeDataString(token)}" +
                  $"&fsids=%5B{fsId}%5D&dlink=1";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var errno = IntOr(json, "errno", 0);
        if (errno != 0) throw new BaiduApiException(errno, DescribeErrNo(errno));

        if (!json.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            var dlink = Str(item, "dlink");
            if (dlink.Length == 0) continue;
            // 官方返回的 dlink 里 & 常被转义成 \u0026，直接拼进 URL 会 400
            dlink = dlink.Replace("\\u0026", "&");
            // 文档要求使用时拼接 access_token
            var sep = dlink.Contains('?') ? "&" : "?";
            return $"{dlink}{sep}access_token={Uri.EscapeDataString(token)}";
        }
        return null;
    }

    /// <summary>
    /// 按需下载到本地。返回 null = 云端没有这个文件（调用方区分「文件不存在」与「下载失败」）。
    /// 下载是实现里真正会撞上限速的一步，因此支持进度回调与取消。
    /// </summary>
    public async Task<string?> DownloadAsync(string netPath, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var dlink = await GetDownloadLinkAsync(netPath, ct).ConfigureAwait(false);
        if (dlink == null) return null;

        var expected = (await FindAsync(netPath, ct).ConfigureAwait(false))?.Size ?? -1;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        // 先写临时文件再改名：中途失败/取消不会在缓存里留半个文件冒充完整副本
        var temp = localPath + ".part";

        try
        {
            using var resp = await Http.GetAsync(dlink, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new BaiduApiException(31326, "下载被拒绝（防盗链校验未通过），请检查网络环境后重试。");
                throw new BaiduApiException(2, $"下载失败：HTTP {(int)resp.StatusCode}。");
            }

            var total = resp.Content.Headers.ContentLength ?? expected;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (File.Exists(localPath)) File.Delete(localPath);
            File.Move(temp, localPath);
            progress?.Report(1.0);
            return localPath;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    // ══════════════════ 沙箱与错误 ══════════════════

    /// <summary>
    /// 沙箱守卫：任何路径都必须落在 /apps 之下。
    /// 这不是"防手滑"，而是把「AI 只能引用用户点过的文件」这条红线的最后一道兜底 ——
    /// 即使上游漏了校验，这里也不会让请求打到用户网盘的其他位置。
    ///
    /// <paramref name="allowSandboxRoot"/>：是否放行 <c>/apps</c> 本身。
    /// <b>只有只读的列目录该传 true</b> —— 写操作的目标永远是具体路径，不会等于 /apps。
    /// </summary>
    /// <remarks>
    /// 2026-09-16 事故：早期版本一律要求 <c>/apps/</c> 前缀，于是「列 /apps」这种合法只读请求
    /// 也被拒，而 <see cref="EnsureDirectoryAsync"/> 逐级建目录时**必然要列一次 /apps** ——
    /// 结果测试连接报「拒绝访问沙箱以外的路径（/apps）」，上传 100% 失败。
    /// 教训：守卫的检查粒度要跟操作的语义对齐，别拿一把尺子量所有请求。
    /// </remarks>
    public static void EnsureSandboxed(string? netPath, bool allowSandboxRoot = false)
    {
        if (string.IsNullOrWhiteSpace(netPath))
            throw new BaiduApiException(2, "网盘路径不能为空。");
        if (allowSandboxRoot && netPath.TrimEnd('/') == SandboxPrefix && netPath.Length > 0) return;
        if (!netPath.StartsWith(SandboxPrefix + "/", StringComparison.Ordinal))
            throw new BaiduApiException(-7,
                $"拒绝访问沙箱以外的路径（{netPath}）。本应用只能读写自己的应用目录 {SandboxPrefix}/{{应用名}}。");
        if (netPath.Contains("..", StringComparison.Ordinal))
            throw new BaiduApiException(-7, "网盘路径中不允许出现「..」。");
    }

    public static string NormalizeNetRoot(string? netRoot)
    {
        var v = (netRoot ?? "").Trim();
        if (v.Length == 0) v = DefaultNetRoot;
        if (!v.StartsWith('/')) v = "/" + v;
        v = v.TrimEnd('/');
        if (!v.StartsWith(SandboxPrefix + "/", StringComparison.Ordinal) && v != SandboxPrefix)
            return DefaultNetRoot;   // 越界配置一律回退默认，不让它变成运行时炸弹
        return v;
    }

    private static string ParentOf(string netPath)
    {
        var idx = netPath.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : netPath[..idx];
    }

    /// <summary>把官方错误码翻成人话（只做翻译，不据此推断业务状态）。</summary>
    public static string DescribeErrNo(int errno) => errno switch
    {
        0 => "成功",
        -6 => "授权失败或已过期，请重新授权",
        -7 => "文件或目录名不合法，或没有访问权限",
        -8 => "文件已存在",
        -9 => "文件或目录不存在",
        -10 => "网盘容量不足",
        -11 => "文件超过大小限制",
        2 => "请求参数有误",
        111 => "当前用户未授权",
        112 => "授权已过期，请重新授权",
        31061 => "同名文件已存在",
        31066 => "文件不存在",
        31069 => "文件正在上传中",
        31299 or 31364 or 31365 => "上传分片参数不符合平台要求（分片大小或数量）",
        31023 => "分片数量超过上限",
        31326 => "下载被拒绝（防盗链校验未通过）",
        20012 => "请求超出配额上限，稍后会自动重试",
        31034 => "触发平台频率控制，稍后会自动重试",
        _ => $"平台返回错误（errno={errno}）",
    };

    // ══════════════════ HTTP 基座 ══════════════════

    private static async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct, bool treatMissingAsEmpty = false)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = TryParse(body);
        if (doc == null)
        {
            if (treatMissingAsEmpty) return EmptyObject();
            throw new BaiduApiException(2, $"接口返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");
        }
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = TryParse(body);
        if (doc == null)
            throw new BaiduApiException(2, $"接口返回了无法解析的内容（HTTP {(int)resp.StatusCode}）。");
        return doc.RootElement.Clone();
    }

    private static JsonDocument? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body); } catch (JsonException) { return null; }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";

    private static long LongOr(JsonElement e, string name, long fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        return fallback;
    }

    private static int IntOr(JsonElement e, string name, int fallback = 0)
    {
        var v = LongOr(e, name, fallback);
        return v is > int.MinValue and < int.MaxValue ? (int)v : fallback;
    }

    private static DateTime FromUnix(long seconds)
    {
        if (seconds <= 0) return DateTime.MinValue;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>分片与内容校验用的 MD5（小写十六进制，与百度的 block_list 口径一致）。</summary>
    public static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
}
