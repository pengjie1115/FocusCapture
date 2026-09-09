using System.Windows;
using System.Windows.Media;

namespace FocusCapture.Windows;

/// <summary>
/// 轻量单行输入对话框（代码构造，无 XAML）：AI 会话重命名 / 新建分组等复用。
/// ShowDialog 返回；确认后经 Input 属性取值，取消返回 null 语义（IsConfirmed=false）。
/// </summary>
public static class PromptDialog
{
    /// <summary>弹出输入框。确认返回输入文本（可空串），取消返回 null。</summary>
    public static string? Show(Window owner, string title, string label, string initial = "")
    {
        var text = new System.Windows.Controls.TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };

        var ok = new System.Windows.Controls.Button
        {
            Content = "确定",
            Width = 72,
            IsDefault = true,
            Margin = new Thickness(0, 12, 8, 0),
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 72,
            IsCancel = true,
            Margin = new Thickness(0, 12, 0, 0),
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = text.Text.Trim();
            Window.GetWindow(ok)!.Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            MaxWidth = 480,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Content = panel,
        };
        text.Focus();
        text.SelectAll();
        win.ShowDialog();
        return result;
    }
}
