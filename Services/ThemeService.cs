namespace FocusCapture.Services;

/// <summary>
/// 主题切换抽象层。
/// 当前支持 Dark / Light，先只对 VoiceInputWindow 生效，后续其他窗口可复用。
/// </summary>
public class ThemeService
{
    public string CurrentTheme { get; private set; } = "Dark";

    /// <summary>设置主题并返回变更后的颜色方案</summary>
    public ThemeColors SetTheme(string theme)
    {
        CurrentTheme = theme;
        return GetColors();
    }

    public ThemeColors GetColors()
    {
        return CurrentTheme switch
        {
            "Light" => ThemeColors.Light,
            _ => ThemeColors.Dark
        };
    }
}

/// <summary>窗口级主题颜色方案（不含控件细节，仅窗口背景/标题栏/边框）</summary>
public record ThemeColors(
    string WindowBg,
    string TitleBarBg,
    string BorderColor,
    string TextColor,
    string SecondaryText,
    string Accent,
    string SplitterColor,
    string BodyBg
)
{
    public static readonly ThemeColors Dark = new(
        WindowBg: "#2D2D2D",
        TitleBarBg: "#333333",
        BorderColor: "#3A3A3A",
        TextColor: "#D0D0D0",
        SecondaryText: "#888888",
        Accent: "#4CAF50",
        SplitterColor: "#3A3A3A",
        BodyBg: "#262626"
    );

    public static readonly ThemeColors Light = new(
        WindowBg: "#EEEEEE",
        TitleBarBg: "#D8D8D8",
        BorderColor: "#C0C0C0",
        TextColor: "#333333",
        SecondaryText: "#666666",
        Accent: "#2E7D32",
        SplitterColor: "#C0C0C0",
        BodyBg: "#F5F5F5"
    );
}
