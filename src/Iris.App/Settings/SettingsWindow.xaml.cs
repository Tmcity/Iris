using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Iris.Core.Settings;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Iris.App.Settings;

internal partial class SettingsWindow : FluentWindow
{
    private const double CompactLayoutThreshold = 680.0;
    private bool? _isCompactMode;
    private bool _suppressPasswordChanged;
    private Point _dragStartPoint;
    private bool _isDragging;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
        SizeChanged += (s, e) => ApplyAdaptiveLayout(e.NewSize.Width);
        Loaded += (s, e) => ApplyAdaptiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        ApplyAdaptiveLayout(Width);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == WidthProperty && e.NewValue is double newWidth && !double.IsNaN(newWidth) && newWidth > 0)
        {
            ApplyAdaptiveLayout(newWidth);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged)
        {
            ApplyAdaptiveLayout(sizeInfo.NewSize.Width);
        }
    }

    internal void ApplyAdaptiveLayout(double width)
    {
        if (TranslationLayoutGrid == null || ProfilesPane == null || DetailsPane == null)
        {
            return;
        }

        bool compact = width < CompactLayoutThreshold;
        if (_isCompactMode == compact)
        {
            return;
        }
        _isCompactMode = compact;

        if (compact)
        {
            ProfilesColDef.Width = new GridLength(1, GridUnitType.Star);
            DetailsColDef.Width = new GridLength(0);
            ProfilesRowDef.Height = new GridLength(96);
            DetailsRowDef.Height = new GridLength(1, GridUnitType.Star);

            Grid.SetRow(ProfilesPane, 0);
            Grid.SetColumn(ProfilesPane, 0);
            ProfilesPane.Margin = new Thickness(0, 0, 0, 8);

            Grid.SetRow(DetailsPane, 1);
            Grid.SetColumn(DetailsPane, 0);
            DetailsPane.Margin = new Thickness(0);
        }
        else
        {
            ProfilesColDef.Width = new GridLength(220);
            DetailsColDef.Width = new GridLength(1, GridUnitType.Star);
            ProfilesRowDef.Height = new GridLength(1, GridUnitType.Star);
            DetailsRowDef.Height = new GridLength(0);

            Grid.SetRow(ProfilesPane, 0);
            Grid.SetColumn(ProfilesPane, 0);
            ProfilesPane.Margin = new Thickness(0, 0, 10, 0);

            Grid.SetRow(DetailsPane, 0);
            Grid.SetColumn(DetailsPane, 1);
            DetailsPane.Margin = new Thickness(0);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel oldVm)
        {
            oldVm.RequestClose -= OnRequestClose;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (oldVm.BeforeSave == FlushFocusedBinding)
            {
                oldVm.BeforeSave = null;
            }
        }

        if (e.NewValue is SettingsViewModel newVm)
        {
            newVm.RequestClose += OnRequestClose;
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            newVm.BeforeSave = FlushFocusedBinding;
            newVm.SetWindowHandleProvider(() => new WindowInteropHelper(this).Handle);
            SyncPasswordBoxWithDraft(newVm.SelectedProfile);
        }
    }

    private void SyncPasswordBoxWithDraft(TranslationProfileDraftViewModel? profile)
    {
        var pb = FindName("ApiKeyPasswordBox") as PasswordBox;
        if (pb == null)
        {
            return;
        }

        _suppressPasswordChanged = true;
        try
        {
            pb.Password = profile?.ReplacementApiKey ?? string.Empty;
        }
        finally
        {
            _suppressPasswordChanged = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedProfile))
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => OnViewModelPropertyChanged(sender, e));
                return;
            }

            if (sender is SettingsViewModel vm)
            {
                SyncPasswordBoxWithDraft(vm.SelectedProfile);
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
                if (vm.IsAnyRecording)
                {
                    _ = vm.CancelRecordingAsync();
                    e.Handled = true;
                    return;
                }

                vm.ExecuteCancel();
                e.Handled = true;
            }
        }
    }

    private void OnNavGeneralClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            if (vm.IsAnyRecording)
            {
                _ = vm.CancelRecordingAsync();
            }
            vm.CurrentPageKey = "general";
        }
    }

    private void OnNavTranslationClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            if (vm.IsAnyRecording)
            {
                _ = vm.CancelRecordingAsync();
            }
            vm.CurrentPageKey = "translation";
        }
    }

    private void OnNavHotkeysClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            if (vm.IsAnyRecording)
            {
                _ = vm.CancelRecordingAsync();
            }
            vm.CurrentPageKey = "hotkeys";
        }
    }

    private void OnNavSelectionClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.CurrentPageKey = "selection";
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
        if (DataContext is SettingsViewModel vm)
        {
            SyncPasswordBoxWithDraft(vm.SelectedProfile);
        }
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged)
        {
            return;
        }

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
        FlushSelectionText(SelectionTimeoutTextBox);
        FlushSelectionText(SelectionProcessesTextBox);
        var focused = FocusManager.GetFocusedElement(this) ?? Keyboard.FocusedElement;
        if (focused is TextBox tb && tb != SelectionTimeoutTextBox && tb != SelectionProcessesTextBox)
        {
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();
        }
    }

    private static void FlushSelectionText(TextBox textBox)
    {
        var binding = textBox.GetBindingExpression(TextBox.TextProperty);
        // Do not overwrite a newly updated draft with a target awaiting data binding.
        if (binding?.IsDirty == true) binding.UpdateSource();
    }

    private void OnProfilesListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void OnProfilesListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging)
        {
            return;
        }

        var currentPoint = e.GetPosition(null);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is ListBox && e.OriginalSource is DependencyObject source)
            {
                var item = FindAncestor<ListBoxItem>(source);
                if (item?.DataContext is TranslationProfileDraftViewModel draggedProfile)
                {
                    _isDragging = true;
                    try
                    {
                        DragDrop.DoDragDrop(item, draggedProfile, DragDropEffects.Move);
                    }
                    finally
                    {
                        _isDragging = false;
                    }
                }
            }
        }
    }

    private void OnProfilesListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(TranslationProfileDraftViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnProfilesListDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.CanEdit)
        {
            return;
        }

        if (e.Data.GetDataPresent(typeof(TranslationProfileDraftViewModel)) &&
            e.Data.GetData(typeof(TranslationProfileDraftViewModel)) is TranslationProfileDraftViewModel sourceProfile)
        {
            if (e.OriginalSource is DependencyObject targetElement)
            {
                var targetItem = FindAncestor<ListBoxItem>(targetElement);
                if (targetItem?.DataContext is TranslationProfileDraftViewModel targetProfile)
                {
                    int oldIndex = vm.Profiles.IndexOf(sourceProfile);
                    int newIndex = vm.Profiles.IndexOf(targetProfile);
                    if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                    {
                        vm.MoveProfile(oldIndex, newIndex);
                        vm.SelectedProfile = sourceProfile;
                    }
                }
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }
            if (current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            else
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return null;
    }
}
