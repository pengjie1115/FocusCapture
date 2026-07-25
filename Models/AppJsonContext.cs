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
internal partial class AppJsonContext : JsonSerializerContext
{
}
