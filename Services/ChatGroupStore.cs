using System.Text.Json;

namespace FocusCapture.Services;

/// <summary>AI 会话分组（一层平铺，不嵌套；会话通过 SessionFile.GroupId 引用，空 = 未分组）</summary>
public class ChatGroup
{
    public string Id { get; set; } = "";     // 分组唯一 ID（GUID）
    public string Name { get; set; } = "";   // 分组名（跨端同名分组合并按名称裁决，规则见方案文档 4.4）
    public DateTime CreatedAt { get; set; }  // 创建时间（同名合并：胜出者 = 创建时间最早）
    public string DeviceId { get; set; } = ""; // 创建端设备 ID（同名合并平局 tie-break：DeviceId 字典序小者胜；
                                               // 旧清单缺该字段由 ChatSyncEngine.SyncGroupsAsync 兜底填充）
}

/// <summary>
/// 会话分组清单读写（%APPDATA%\FocusCapture\chat_groups.json，独立文件，参与坚果云同步）。
/// 序列化方案定稿：独立 JsonSerializerOptions（与 SessionFile 同风格反射序列化，环境反射可用）。
/// 多端同名分组合并 UI 与规则执行留阶段二；本类只负责读写，阶段二直接调用。
/// </summary>
public static class ChatGroupStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FocusCapture", "chat_groups.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>读取分组清单；文件不存在/损坏返回空清单（不崩，首次保存时重建）</summary>
    public static List<ChatGroup> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<ChatGroup>();
            var groups = JsonSerializer.Deserialize<List<ChatGroup>>(File.ReadAllText(StorePath, Encoding.UTF8), JsonOptions);
            return groups ?? new List<ChatGroup>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[FocusCapture] 会话分组清单读取失败: {ex.Message}");
            return new List<ChatGroup>();
        }
    }

    /// <summary>保存分组清单（全量覆盖写）</summary>
    public static void Save(List<ChatGroup> groups)
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(groups, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FocusCapture] 会话分组清单保存失败: {ex.Message}");
        }
    }

    /// <summary>新建分组（GUID 主键，查重交调用方；阶段二分组 UI 调用）</summary>
    public static ChatGroup Create(string name)
    {
        return new ChatGroup { Id = Guid.NewGuid().ToString(), Name = name, CreatedAt = DateTime.Now };
    }
}
