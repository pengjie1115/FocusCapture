using System.Threading;
using FocusCapture.Services.Files;

namespace FocusCapture.Services.Baidu;

/// <summary>
/// 把 <see cref="BaiduNetdiskClient"/> 接到文件仓库的 <see cref="ICloudStorage"/> 上。
///
/// 只做适配，不加业务：凭据齐全 + 有令牌才算「可用」，否则文件仓库自动降级为「只存本地」，
/// 而不是让用户撞一堆莫名其妙的异常。
/// </summary>
public sealed class BaiduCloudStorage : ICloudStorage
{
    public BaiduCloudStorage(string netRoot)
    {
        NetRoot = BaiduNetdiskClient.NormalizeNetRoot(netRoot);
    }

    public string NetRoot { get; }

    /// <summary>凭据齐 + 本机有令牌（令牌过期但有 refresh_token 也算可用，客户端会自己刷）。</summary>
    public bool IsReady
    {
        get
        {
            try
            {
                var creds = BaiduCredentialStore.LoadCredentials();
                if (creds?.IsComplete != true) return false;
                var token = BaiduCredentialStore.LoadToken();
                return token != null && token.RefreshToken.Length > 0;
            }
            catch { return false; }
        }
    }

    private BaiduNetdiskClient Client()
    {
        var creds = BaiduCredentialStore.LoadCredentials()
                    ?? throw new BaiduAuthRequiredException("尚未配置百度网盘应用凭据（AppKey / SecretKey）。");
        return new BaiduNetdiskClient(creds, NetRoot);
    }

    public async Task UploadAsync(string localPath, string netPath, IProgress<double>? progress, CancellationToken ct)
        => await Client().UploadFileAsync(localPath, netPath, progress, ct).ConfigureAwait(false);

    public async Task<bool> DownloadAsync(string netPath, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var result = await Client().DownloadAsync(netPath, localPath, progress, ct).ConfigureAwait(false);
        return result != null;
    }

    public async Task DeleteAsync(IEnumerable<string> netPaths, CancellationToken ct)
    {
        var list = netPaths.ToList();
        if (list.Count == 0) return;
        try
        {
            await Client().DeleteAsync(list, ct).ConfigureAwait(false);
        }
        catch (BaiduApiException ex) when (ex.ErrNo is -9 or 31066)
        {
            // 云端本来就没有 = 删除的期望状态已达成，不当作失败（幂等）
            AppLog.Info("Baidu", "删除时云端已不存在该文件，视为成功");
        }
    }

    public async Task EnsureDirectoryAsync(string netDir, CancellationToken ct)
        => await Client().EnsureDirectoryAsync(netDir, ct).ConfigureAwait(false);

    // ── 供设置面板使用的授权动作（不走 ICloudStorage，属初始化流程） ──

    /// <summary>
    /// 连通性自检：授权是否真的能用（建目录 + 列目录，两层都验）。
    ///
    /// 特意把「建目录」放进自检：**能不能写**才是用户关心的，只测列目录会给出假绿。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(string netRoot, CancellationToken ct)
    {
        try
        {
            var creds = BaiduCredentialStore.LoadCredentials();
            if (creds?.IsComplete != true) return (false, "请先填写 AppKey 与 SecretKey。");

            var client = new BaiduNetdiskClient(creds, netRoot);

            try
            {
                await client.EnsureDirectoryAsync(client.NetRoot, ct).ConfigureAwait(false);
            }
            catch (BaiduApiException ex) when (ex.ErrNo is -6 or 111 or 112)
            {
                return (false, "授权无效或已过期，请重新点「开始授权」。");
            }
            catch (BaiduApiException ex)
            {
                return (false, $"{ex.Message}\n" +
                               $"请到百度网盘开放平台核对应用的名称，本机填的网盘目录必须与它一致" +
                               $"（当前填的是 {client.NetRoot}）。");
            }

            var entries = await client.ListAsync(client.NetRoot, ct).ConfigureAwait(false);
            return (true, $"连接成功：已能读写 {client.NetRoot}（现有 {entries.Count} 个条目）。");
        }
        catch (BaiduAuthRequiredException ex)
        {
            return (false, ex.Message);
        }
        catch (BaiduApiException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }
}
