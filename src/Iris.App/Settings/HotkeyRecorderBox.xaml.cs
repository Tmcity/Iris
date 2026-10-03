using System.Windows.Controls;
using System.Windows.Input;

namespace Iris.App.Settings;

/// <summary>
/// 快捷键录入交互控件。
/// 支持只读展示、Enter/Space 开始录入、Esc/离开取消。
/// 规则见 HOTKEY_RECORDING_UI.md §7。
/// </summary>
internal partial class HotkeyRecorderBox : UserControl
{
    public HotkeyRecorderBox()
    {
        InitializeComponent();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeyItemDraftViewModel vm)
        {
            return;
        }

        if (!vm.IsRecording)
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                if (vm.StartRecordCommand.CanExecute(null))
                {
                    vm.StartRecordCommand.Execute(null);
                    e.Handled = true;
                }
            }
            else if (e.Key is Key.Back or Key.Delete)
            {
                if (vm.ClearCommand.CanExecute(null))
                {
                    vm.ClearCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is HotkeyItemDraftViewModel vm && vm.IsRecording)
        {
            // 鼠标离开当前录入控件时取消录入并收尾
            if (vm.CancelRecordCommand.CanExecute(null))
            {
                vm.CancelRecordCommand.Execute(null);
            }
        }
    }
}
