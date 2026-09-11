namespace FocusCapture;

/// <summary>
/// 数据根目录唯一入口（2026-09-11 引入，为自动化测试提供数据隔离能力）。
///
/// 背景：改造前 `%AppData%\FocusCapture\` 被硬编码散落在十余个文件中，
/// 导致测试无法在不触碰用户真实数据的前提下运行（回收站记录 deleted.json 等正在此目录下）。
///
/// 行为契约：
/// - <see cref="RootOverride"/> 为 null（生产默认）→ Root 与改造前的 `%AppData%\FocusCapture` 完全一致，行为不变
/// - <see cref="RootOverride"/> 被设置（仅测试 / 隔离场景）→ 所有落盘路径改道到该目录
///
/// 实现注意：Root 必须是**动态求值的属性**，不能是 static readonly 字段 ——
/// 类型初始化时缓存会导致设置 RootOverride 后不生效（存量代码已有此先例，如 AppSettings.ConfigPath）。
///
/// 命名空间刻意置于根 `FocusCapture`，使所有 `FocusCapture.*` 子命名空间可直接引用，无需逐文件加 using。
/// </summary>
public static class FocusCapturePaths
{
    /// <summary>应用数据目录名（默认根目录的末级）。</summary>
    public const string AppFolderName = "FocusCapture";

    private static string? _rootOverride;

    /// <summary>根目录覆盖值；null 或空白 = 使用默认真实目录。仅测试与隔离场景设置。</summary>
    public static string? RootOverride
    {
        get => _rootOverride;
        set => _rootOverride = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>数据根目录（动态求值，不缓存）。</summary>
    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    /// <summary>在数据根目录下拼接子路径。</summary>
    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    /// <summary>创建并返回根目录。</summary>
    public static string EnsureRoot()
    {
        var root = Root;
        Directory.CreateDirectory(root);
        return root;
    }
}
