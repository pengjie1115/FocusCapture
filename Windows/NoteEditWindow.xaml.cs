using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 全屏笔记编辑窗口：独立 Window（可拖动/缩放），供长文本编辑。
/// 与行内编辑共享 NoteEntryViewModel.EditText（双向同步）。
/// 保存：Ctrl+S / 保存按钮；取消：Esc / 取消按钮。
/// </summary>
public partial class NoteEditWindow : Window
{
    private readonly NoteService _noteService;
    private readonly NoteEntryViewModel _vm;

    public NoteEditWindow(NoteService noteService, NoteEntryViewModel vm, string title)
    {
        _noteService = noteService;
        _vm = vm;
        InitializeComponent();
        Title = title;
        NoteInfoText.Text = $"{title}  ·  来源: {vm.SourceWindow}";
        // 同步行内编辑的最新内容（打开全屏前可能已在行内改过）
        EditBox.Text = vm.EditText;
        EditBox.Focus();
        EditBox.CaretIndex = 0;
        EditBox.ScrollToHome();
        UpdateCharCount();
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCharCount();

    private void UpdateCharCount()
    {
        var len = EditBox.Text.Length;
        CharCountText.Text = $"{len} 字符";
    }

    private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(); // 取消：不保存
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Save();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (ImmersiveSessionService.IsLocked(_vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newContent = EditBox.Text.Trim();
        if (string.IsNullOrEmpty(newContent))
        {
            System.Windows.MessageBox.Show(this, "内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _vm.EditText = newContent;
        if (_noteService.UpdateNote(_vm.Entry, newContent))
        {
            DialogResult = true;
            Close();
        }
        else
        {
            System.Windows.MessageBox.Show(this, "保存失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
