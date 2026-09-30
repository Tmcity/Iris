using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Iris.App.Settings;

internal partial class SettingsWindow : FluentWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel oldVm)
        {
            oldVm.RequestClose -= OnRequestClose;
        }

        if (e.NewValue is SettingsViewModel newVm)
        {
            newVm.RequestClose += OnRequestClose;
        }
    }

    private void OnRequestClose()
    {
        Close();
    }

    internal bool ForceClose { get; set; }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ForceClose)
        {
            return;
        }

        if (DataContext is SettingsViewModel vm && vm.IsSaving)
        {
            // 保存进行中暂不受理普通关闭
            e.Cancel = true;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is SettingsViewModel vm && !vm.IsSaving)
            {
                vm.ExecuteCancel();
                e.Handled = true;
            }
        }
    }
}
