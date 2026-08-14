using System.Text.Json.Serialization;
using FocusCapture.Models;

namespace FocusCapture;

/// <summary>
/// 编译期 JSON 序列化上下文（Source Generator），
/// 兼容 PublishTrimmed / IsReflectionEnabledByDefault=false 环境。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HotkeyBinding))]
[JsonSerializable(typeof(ExportConfig))]
[JsonSerializable(typeof(DeletedNote))]
[JsonSerializable(typeof(List<DeletedNote>))]
// QUEST-5：SyncSettings 在 AppSettings 内走源生成，漏注册运行时抛 NotSupportedException。
// SyncNote 由 [JsonPropertyName] 锁定 camelCase，与独立 SyncJson.Options（桶文件）命名一致。
[JsonSerializable(typeof(SyncSettings))]
[JsonSerializable(typeof(SyncNote))]
[JsonSerializable(typeof(List<SyncNote>))]
[JsonSerializable(typeof(SyncBucket))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
