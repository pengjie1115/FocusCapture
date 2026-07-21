using System.Windows.Forms;
using FocusCapture.Models;

namespace FocusCapture.Windows;

public partial class ExportDialog : Window
{
    private readonly AppSettings _settings;

    public ExportConfig Config { get; private set; } = new();
    public bool Confirmed { get; private set; }
    public string FolderPath { get; private set; } = "";

    public ExportDialog(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // 加载上次配置
        Config = settings.LastExportConfig ?? new ExportConfig();
        ChkTime.IsChecked = Config.IncludeTime;
        ChkSource.IsChecked = Config.IncludeSource;
        ChkContent.IsChecked = Config.IncludeContent;

        RbMarkdown.IsChecked = Config.Format == ExportFormat.Markdown;
        RbWord.IsChecked = Config.Format == ExportFormat.Word;
        RbTxt.IsChecked = Config.Format == ExportFormat.Txt;

        UpdateFolderDisplay();
    }

    private void UpdateFolderDisplay()
    {
        TxtFolderPath.Text = string.IsNullOrWhiteSpace(_settings.ExportFolderPath)
            ? "(未设置 — 导出时会提示选择)"
            : _settings.ExportFolderPath;
    }

    private void BtnChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择默认导出文件夹",
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(_settings.ExportFolderPath))
            dialog.SelectedPath = _settings.ExportFolderPath;
        else
            dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _settings.ExportFolderPath = dialog.SelectedPath;
            _settings.Save();
            UpdateFolderDisplay();
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        // 读取勾选状态
        Config = new ExportConfig
        {
            IncludeTime = ChkTime.IsChecked == true,
            IncludeSource = ChkSource.IsChecked == true,
            IncludeContent = ChkContent.IsChecked == true,
            Format = RbWord.IsChecked == true ? ExportFormat.Word
                : RbTxt.IsChecked == true ? ExportFormat.Txt
                : ExportFormat.Markdown
        };

        // 持久化配置偏好
        _settings.LastExportConfig = Config;

        // 确定保存文件夹
        if (string.IsNullOrWhiteSpace(_settings.ExportFolderPath))
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择保存位置",
                ShowNewFolderButton = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return; // 用户取消

            FolderPath = dialog.SelectedPath;
            _settings.ExportFolderPath = FolderPath;
            _settings.Save();
        }
        else
        {
            FolderPath = _settings.ExportFolderPath;
        }

        Confirmed = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
