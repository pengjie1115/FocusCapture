namespace FocusCapture.Services;

/// <summary>
/// 权限检查中心（门卫）。
/// 骨架阶段：全局开关默认关闭，所有功能放行，行为与未接线时完全一致。
/// 将来收费只需三步（各功能接线点无需再改动）：
///   1) IsLicensingEnabled = true
///   2) 在收费功能登记处把功能键加入 _paid
///   3) 实现 IsLicenseValid（本地激活 / 在线验证）
/// </summary>
public static class LicenseGate
{
    // ── 功能名单：已接线的功能在此登记（报到）──
    public const string FeatureAiChat = "ai_chat";
    public const string FeatureVoiceInput = "voice_input";
    public const string FeatureExport = "export";
    public const string FeatureSync = "sync";
    public const string FeatureSearch = "search";

    /// <summary>全局开关：false = 免费开放全部；true = 按功能策略拦截。</summary>
    public static bool IsLicensingEnabled { get; set; } = false;

    /// <summary>收费功能集合。骨架阶段为空；将来在此登记，例如 _paid.Add(FeatureAiChat)。</summary>
    private static readonly HashSet<string> _paid = new();

    /// <summary>功能是否允许使用（所有接线点的唯一检查入口）。</summary>
    public static bool IsAllowed(string featureKey)
    {
        if (!IsLicensingEnabled) return true;           // 全局开关关 = 放行
        if (!_paid.Contains(featureKey)) return true;   // 未标收费 = 放行
        return IsLicenseValid();                        // 收费功能 → 验票
    }

    /// <summary>带提示的检查：UI 接线点一行调用。不通过时弹窗并返回 false。</summary>
    public static bool EnsureAllowed(string featureKey, string featureName)
    {
        if (IsAllowed(featureKey)) return true;
        MessageBox.Show(
            $"「{featureName}」是 FocusCapture 专业版功能，购买后即可使用。",
            "FocusCapture",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    /// <summary>许可证是否有效。骨架阶段恒 false；将来接本地激活/在线验证后实现。</summary>
    private static bool IsLicenseValid() => false;
}
