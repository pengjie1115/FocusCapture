using System.Windows.Forms;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class ExportDialog : Window
{
    private readonly AppSettings _settings;
    private readonly NoteService _noteService;   // 2026-08-15 新增：导入需要复用现有实例触发 NotesChanged

    public ExportConfig Config { get; private set; } = new();
    public bool Confirmed { get; private set; }
    public string FolderPath { get; private set; } = "";

    public ExportDialog(AppSettings settings, NoteService noteService)
    {
        InitializeComponent();
        _settings = settings;
        _noteService = noteService;

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

    /// <summary>导入按钮：打开文件选择 → 解析 TXT/MD/Word → 弹预览窗让用户勾选 + 选目标日期 → 写入 MD。</summary>
    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的文件",
                Filter = NoteImportService.FormatFilter,
                CheckFileExists = true,
                Multiselect = false
            };
            // 默认起始目录：当前导出生效文件夹 或 我的文档
            var startDir = !string.IsNullOrWhiteSpace(_settings.ExportFolderPath) && Directory.Exists(_settings.ExportFolderPath)
                ? _settings.ExportFolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            dlg.InitialDirectory = startDir;

            if (dlg.ShowDialog(this) != true) return;
            var path = dlg.FileName;

            NoteImportService.ImportPreview? preview;
            try
            {
                preview = new NoteImportService().Parse(path);
            }
            catch (Exception parseEx)
            {
                System.Windows.MessageBox.Show($"解析失败：{parseEx.Message}", "导入失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (preview.Entries.Count == 0)
            {
                System.Windows.MessageBox.Show("文件中没有可识别的笔记内容。", "导入",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var previewDialog = new ImportPreviewDialog(preview, _noteService) { Owner = this };
            if (previewDialog.ShowDialog() != true) return;

            // 复用项目已有的 NoteService 实例（保证 NotesChanged 事件被同步引擎订阅到）
            var written = _noteService.ImportNotes(previewDialog.SelectedEntries, previewDialog.TargetDate);

            System.Windows.MessageBox.Show($"成功导入 {written} 条笔记到 {previewDialog.TargetDate:yyyy-MM-dd}。",
                "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导入失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
