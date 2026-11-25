using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Iris.App.Settings;

// List -> detail drill-down for the translation-provider page and the OCR page.
// Whether a page shows its detail view is view-only state on SettingsViewModel
// (IsTranslationDetailOpen / IsOcrDetailOpen). Every transition is driven from this
// file, so no existing view-model method has to know about the drill-down.
internal partial class SettingsWindow
{
    // Id of the item whose detail view is open; null while the page shows its list.
    private string? _translationDetailId;
    private string? _ocrDetailId;

    // Read-only views of the two ids, for tests.
    internal string? TranslationDetailId => _translationDetailId;
    internal string? OcrDetailId => _ocrDetailId;

    private bool _ocrRestorePending;
    private bool _ocrAddPending;
    private HashSet<string>? _ocrIdsBeforeAdd;

    // Called when the view model reports a new CurrentPageKey. The view model raises
    // that notification only when the normalized key actually changes, so writing the
    // key of the page that is already showing keeps an open detail view.
    private void ResetDetailNavigation(SettingsViewModel vm)
    {
        vm.IsTranslationDetailOpen = false;
        vm.IsOcrDetailOpen = false;
        _translationDetailId = null;
        _ocrDetailId = null;
        _translationRestorePending = false;
        _ocrRestorePending = false;
        _ocrAddPending = false;
        _ocrIdsBeforeAdd = null;
    }

    private bool _translationRestorePending;

    private void TryOpenTranslationDetail(TranslationProfileDraftViewModel? profile)
    {
        if (profile is null || DataContext is not SettingsViewModel vm || vm.IsSaving || _isDragging)
        {
            return;
        }

        if (!ReferenceEquals(ProfilesListBox.SelectedItem, profile))
        {
            ProfilesListBox.SelectedItem = profile;
        }

        if (!ReferenceEquals(vm.SelectedProfile, profile))
        {
            return;
        }

        _translationDetailId = profile.Id;
        vm.IsTranslationDetailOpen = true;

        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TranslationBreadcrumbBackButton.Focus()));
        }
    }

    private void CloseTranslationDetail(SettingsViewModel vm)
    {
        vm.IsTranslationDetailOpen = false;
        _translationDetailId = null;
        _translationRestorePending = false;

        if (IsLoaded && ProfilesListBox.SelectedItem is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (ProfilesListBox.ItemContainerGenerator.ContainerFromItem(ProfilesListBox.SelectedItem) is ListBoxItem item)
                {
                    item.Focus();
                }
            }));
        }
    }

    private void OnTranslationBreadcrumbBackClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            CloseTranslationDetail(vm);
        }
    }

    private void OnTranslationProfileAddedFromPreset(SettingsViewModel vm, string? previousProfileId)
    {
        if (vm.SelectedProfile is { } added && !string.Equals(added.Id, previousProfileId, StringComparison.Ordinal))
        {
            TryOpenTranslationDetail(added);
        }
    }

    private void OnTranslationViewModelSelectionChanged(SettingsViewModel vm)
    {
        if (!vm.IsTranslationDetailOpen || _translationDetailId is null || _translationRestorePending)
        {
            return;
        }

        _translationRestorePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ReconcileTranslationDetailSelection));
    }

    private void ReconcileTranslationDetailSelection()
    {
        _translationRestorePending = false;

        if (DataContext is not SettingsViewModel vm || !vm.IsTranslationDetailOpen || _translationDetailId is null)
        {
            return;
        }

        var target = vm.Profiles.FirstOrDefault(p => string.Equals(p.Id, _translationDetailId, StringComparison.Ordinal));
        if (target is null)
        {
            CloseTranslationDetail(vm);
            return;
        }

        if (!ReferenceEquals(vm.SelectedProfile, target))
        {
            vm.SelectedProfile = target;
            ProfilesListBox.SelectedItem = target;
        }
    }

    private void OnTranslationRowEntryMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranslationProfileDraftViewModel profile })
        {
            TryOpenTranslationDetail(profile);
        }
    }

    private void OnTranslationRowOpenDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranslationProfileDraftViewModel profile })
        {
            TryOpenTranslationDetail(profile);
        }
    }

    private void OnTranslationProfilesListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not ListBoxItem { DataContext: TranslationProfileDraftViewModel profile })
        {
            return;
        }

        e.Handled = true;
        TryOpenTranslationDetail(profile);
    }

    private static ContextMenu BuildTranslationRowContextMenu(SettingsViewModel vm)
    {
        var menu = new ContextMenu();

        var moveUpItem = new MenuItem
        {
            Header = UiText.SettingsMoveUp,
            IsEnabled = vm.MoveUpCommand.CanExecute(null),
        };
        moveUpItem.Click += (_, _) =>
        {
            if (vm.MoveUpCommand.CanExecute(null))
            {
                vm.MoveUpCommand.Execute(null);
            }
        };
        menu.Items.Add(moveUpItem);

        var moveDownItem = new MenuItem
        {
            Header = UiText.SettingsMoveDown,
            IsEnabled = vm.MoveDownCommand.CanExecute(null),
        };
        moveDownItem.Click += (_, _) =>
        {
            if (vm.MoveDownCommand.CanExecute(null))
            {
                vm.MoveDownCommand.Execute(null);
            }
        };
        menu.Items.Add(moveDownItem);

        var deleteItem = new MenuItem
        {
            Header = UiText.SettingsDeleteProfile,
            IsEnabled = vm.DeleteProfileCommand.CanExecute(null),
        };
        deleteItem.Click += (_, _) =>
        {
            if (vm.DeleteProfileCommand.CanExecute(null))
            {
                vm.DeleteProfileCommand.Execute(null);
            }
        };
        menu.Items.Add(deleteItem);

        return menu;
    }

    private void OnTranslationRowMoreActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: TranslationProfileDraftViewModel profile } button)
        {
            return;
        }

        if (DataContext is not SettingsViewModel vm || !vm.CanEdit || vm.IsSaving)
        {
            return;
        }

        if (!ReferenceEquals(ProfilesListBox.SelectedItem, profile))
        {
            ProfilesListBox.SelectedItem = profile;
        }

        if (!ReferenceEquals(vm.SelectedProfile, profile))
        {
            vm.SelectedProfile = profile;
        }

        var menu = BuildTranslationRowContextMenu(vm);
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnTranslationProfilesListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.CanEdit || vm.IsSaving)
        {
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is { DataContext: TranslationProfileDraftViewModel profile } item)
        {
            if (!ReferenceEquals(ProfilesListBox.SelectedItem, profile))
            {
                ProfilesListBox.SelectedItem = profile;
            }

            if (!ReferenceEquals(vm.SelectedProfile, profile))
            {
                vm.SelectedProfile = profile;
            }

            var menu = BuildTranslationRowContextMenu(vm);
            item.ContextMenu = menu;
            menu.PlacementTarget = item;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void OnOcrBreadcrumbBackClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) CloseOcrDetail(vm);
    }

    private void OnOcrRowEntryMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OcrProfileDraftViewModel profile })
            TryOpenOcrDetail(profile);
    }

    private void OnOcrRowOpenDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OcrProfileDraftViewModel profile })
            TryOpenOcrDetail(profile);
    }

    private void OnOcrProfilesListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not ListBoxItem { DataContext: OcrProfileDraftViewModel profile })
            return;
        e.Handled = true;
        TryOpenOcrDetail(profile);
    }

    private static ContextMenu BuildOcrRowContextMenu(SettingsViewModel vm, OcrProfileDraftViewModel profile)
    {
        var menu = new ContextMenu();
        var deleteItem = new MenuItem
        {
            Header = UiText.OcrDelete,
            IsEnabled = vm.CanEdit && !profile.IsBuiltIn,
        };
        deleteItem.Click += (_, _) =>
        {
            if (vm.DeleteOcrProfileCommand.CanExecute(null))
                vm.DeleteOcrProfileCommand.Execute(null);
        };
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void OnOcrRowMoreActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OcrProfileDraftViewModel profile } button) return;
        if (DataContext is not SettingsViewModel vm || !vm.CanEdit || vm.IsSaving) return;

        if (!ReferenceEquals(OcrProfilesListBox.SelectedItem, profile))
            OcrProfilesListBox.SelectedItem = profile;
        if (!ReferenceEquals(vm.SelectedOcrProfile, profile))
            vm.SelectedOcrProfile = profile;

        var menu = BuildOcrRowContextMenu(vm, profile);
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnOcrProfilesListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.CanEdit || vm.IsSaving)
        {
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is { DataContext: OcrProfileDraftViewModel profile } item)
        {
            if (!ReferenceEquals(OcrProfilesListBox.SelectedItem, profile))
                OcrProfilesListBox.SelectedItem = profile;
            if (!ReferenceEquals(vm.SelectedOcrProfile, profile))
                vm.SelectedOcrProfile = profile;

            var menu = BuildOcrRowContextMenu(vm, profile);
            item.ContextMenu = menu;
            menu.PlacementTarget = item;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void TryOpenOcrDetail(OcrProfileDraftViewModel? profile)
    {
        if (profile is null || DataContext is not SettingsViewModel vm || vm.IsSaving) return;
        // C4 guard: in read-only mode only the already-selected profile may open detail.
        if (!vm.CanEdit && !ReferenceEquals(vm.SelectedOcrProfile, profile)) return;

        if (!ReferenceEquals(OcrProfilesListBox.SelectedItem, profile))
            OcrProfilesListBox.SelectedItem = profile;
        if (!ReferenceEquals(vm.SelectedOcrProfile, profile))
            vm.SelectedOcrProfile = profile;

        _ocrDetailId = profile.Id;
        _ocrRestorePending = false;
        vm.IsOcrDetailOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => OcrBreadcrumbBackButton.Focus()));
    }

    private void CloseOcrDetail(SettingsViewModel vm)
    {
        vm.IsOcrDetailOpen = false;
        _ocrDetailId = null;
        _ocrRestorePending = false;
        if (IsLoaded && OcrProfilesListBox.SelectedItem is { } selected)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (OcrProfilesListBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
                    item.Focus();
            }));
        }
    }

    private void OnAddOcrProfilePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.CanEdit || vm.IsSaving) return;
        _ocrIdsBeforeAdd = vm.OcrProfiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        _ocrAddPending = true;
    }

    private void OnAddOcrProfileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.CanEdit || vm.IsSaving) return;
        if (!_ocrAddPending)
        {
            _ocrIdsBeforeAdd = vm.OcrProfiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            _ocrAddPending = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CheckPendingOcrAdd));
    }

    private void CheckPendingOcrAdd()
    {
        if (!_ocrAddPending || DataContext is not SettingsViewModel vm) return;
        _ocrAddPending = false;
        var before = _ocrIdsBeforeAdd;
        _ocrIdsBeforeAdd = null;
        if (!vm.CanEdit || vm.IsSaving) return;
        var added = before is not null ? vm.OcrProfiles.FirstOrDefault(p => !before.Contains(p.Id)) : null;
        if (added is null) return;
        if (!ReferenceEquals(vm.SelectedOcrProfile, added)) vm.SelectedOcrProfile = added;
        _ocrDetailId = added.Id;
        vm.IsOcrDetailOpen = true;
        if (IsLoaded)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => OcrBreadcrumbBackButton.Focus()));
    }

    private void ScheduleOcrDetailSelectionCheck() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ReconcileOcrDetailSelection));

    private void ReconcileOcrDetailSelection()
    {
        if (DataContext is not SettingsViewModel vm || !vm.IsOcrDetailOpen || _ocrDetailId is null)
        {
            _ocrRestorePending = false;
            return;
        }

        var target = vm.OcrProfiles.FirstOrDefault(p => string.Equals(p.Id, _ocrDetailId, StringComparison.Ordinal));
        if (target is null)
        {
            CloseOcrDetail(vm);
            return;
        }

        if (ReferenceEquals(vm.SelectedOcrProfile, target))
        {
            _ocrRestorePending = false;
            return;
        }

        if (!vm.CanEdit)
        {
            _ocrRestorePending = true;
            return;
        }

        vm.SelectedOcrProfile = target;
        OcrProfilesListBox.SelectedItem = target;
        _ocrRestorePending = false;
    }

    private void OnViewModelCanEditChanged(SettingsViewModel vm)
    {
        if (vm.CanEdit && _ocrRestorePending)
        {
            _ocrRestorePending = false;
            ReconcileOcrDetailSelection();
        }
    }

    private void OnOcrProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel { CanEdit: false } vm &&
            !ReferenceEquals(OcrProfilesListBox.SelectedItem, vm.SelectedOcrProfile))
        {
            OcrProfilesListBox.SelectedItem = vm.SelectedOcrProfile;
        }
        ScheduleOcrDetailSelectionCheck();
    }
}
