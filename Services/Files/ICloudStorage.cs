using System.Threading;

namespace FocusCapture.Services.Files;

/// <summary>
/// 云仓库契约（文件本体的上传 / 取回 / 删除）。
///
/// 为什么抽这层：文件仓库的业务逻辑（元数据、账本、淘汰、查重）不该知道「背后是百度还是别的」。
/// 抽出来还有一个更实际的好处 —— 检查点里可以塞一个内存假实现，把「先本地后云端」「两道防重复防线」
/// 这些规则在无网络环境下验证到位（REGRESSION 铁律：自动化检查点不得联网）。
/// </summary>
public interface ICloudStorage
{
    /// <summary>是否可用（凭据齐备 + 已授权）。false 时文件仓库退化为「只存本地」而不报错。</summary>
    bool IsReady { get; }

    /// <summary>仓库根目录（沙箱内），形如 /apps/FocusCapture。</summary>
    string NetRoot { get; }

    /// <summary>上传单个文件到指定云端路径（覆盖同名）。</summary>
    Task UploadAsync(string localPath, string netPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>取回单个文件。返回 false = 云端没有这份文件。</summary>
    Task<bool> DownloadAsync(string netPath, string localPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>删除云端文件（可批量，幂等：不存在不算失败）。</summary>
    Task DeleteAsync(IEnumerable<string> netPaths, CancellationToken ct);

    /// <summary>确保云端目录存在（逐级创建）。</summary>
    Task EnsureDirectoryAsync(string netDir, CancellationToken ct);
}
