using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Iris.Core.Settings;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;
using MenuItem = System.Windows.Controls.MenuItem;

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
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is SettingsViewModel newVm)
        {
            newVm.RequestClose += OnRequestClose;
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedProfile) or nameof(SettingsViewModel.IsSaving))
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => OnViewModelPropertyChanged(sender, e));
                return;
            }

            var pb = FindName("ApiKeyPasswordBox") as PasswordBox;
            if (pb != null && sender is SettingsViewModel vm && !vm.IsSaving)
            {
                // 保存完成或切换服务时，清空当前密码框输入
                pb.Password = string.Empty;
            }
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

    private void OnNavGeneralClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CurrentPageKey = "general";
        }
    }

    private void OnNavTranslationClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CurrentPageKey = "translation";
        }
    }

    private void OnAddProfileButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var preset in SettingsViewModel.AvailablePresets)
        {
            var item = new MenuItem
            {
                Header = preset.DisplayName,
                Tag = preset
            };
            item.Click += (s, ev) =>
            {
                if (s is MenuItem mi && mi.Tag is TranslationProviderPreset p)
                {
                    vm.AddProfileFromPreset(p);
                }
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = AddProfileButton;
        menu.IsOpen = true;
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var pb = FindName("ApiKeyPasswordBox") as PasswordBox;
        if (pb != null)
        {
            pb.Password = string.Empty;
        }
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.SelectedProfile != null && sender is PasswordBox pb)
        {
            vm.SelectedProfile.SetReplacementApiKey(pb.Password);
        }
    }

    private void OnSaveButtonClick(object sender, RoutedEventArgs e)
    {
        FlushFocusedBinding();
    }

    internal void FlushFocusedBinding()
    {
        var focused = FocusManager.GetFocusedElement(this);
        if (focused is TextBox tb)
        {
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();
        }
    }
}
