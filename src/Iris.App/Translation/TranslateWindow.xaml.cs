using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Iris.Core.Imaging;

namespace Iris.App.Translation;

internal partial class TranslateWindow : Wpf.Ui.Controls.FluentWindow
{
    private TranslateWindowPresenter? _presenter;
    private bool _isUpdatingPlacement;

    public TranslateWindow()
    {
        InitializeComponent();
        DpiChanged += (s, e) => _presenter?.ReconstrainWindowPlacement();
    }

    internal bool ForceClose { get; set; }

    internal TranslateWindowViewModel? ViewModel => DataContext as TranslateWindowViewModel;

    internal void AttachPresenter(TranslateWindowPresenter presenter)
    {
        _presenter = presenter;
        ViewModel?.AttachPresenter(presenter);
    }

    internal void FocusInputArea()
    {
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            finally
            {
                NotifyUserPlacementChanged();
            }
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        _ = _presenter?.HideAsync();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ForceClose)
        {
            e.Cancel = true;
            _ = _presenter?.HideAsync();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _presenter?.OnWindowActivated();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _presenter?.OnWindowDeactivated();
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _presenter?.OnWindowIsVisibleChanged(IsVisible);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = _presenter?.HideAsync();
        }
    }

    internal static Func<ModifierKeys>? TestKeyboardModifiersProvider { get; set; }

    private void OnInputTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = _presenter?.HideAsync();
            return;
        }

        if (e.Key == Key.ImeProcessed)
        {
            // IME 组合输入确认不触发翻译
            return;
        }

        if (e.Key == Key.Enter)
        {
            var vm = ViewModel;
            if (vm == null) return;

            var modifiers = TestKeyboardModifiersProvider != null ? TestKeyboardModifiersProvider() : Keyboard.Modifiers;
            bool isCtrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool isShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (vm.EnterToTranslate)
            {
                if (!isShift && !isCtrl)
                {
                    e.Handled = true;
                    vm.ExecuteTranslateFromUser();
                }
            }
            else
            {
                if (isCtrl)
                {
                    e.Handled = true;
                    vm.ExecuteTranslateFromUser();
                }
            }
        }
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (!_isUpdatingPlacement && IsLoaded)
        {
            NotifyUserPlacementChanged();
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LanguageControlsGrid != null && LanguageActionsPanel != null)
        {
            bool shortWindow = e.NewSize.Height < 440;
            ContentScrollViewer.VerticalScrollBarVisibility = shortWindow
                ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
            ContentScrollViewer.Padding = shortWindow ? new Thickness(0, 0, 12, 0) : new Thickness(0);
            CardsScrollViewer.MinHeight = shortWindow ? 160 : 0;
            CardsScrollViewer.MaxHeight = shortWindow ? 300 : double.PositiveInfinity;
            bool narrow = e.NewSize.Width < 420;
            System.Windows.Controls.Grid.SetColumnSpan(SourceLangComboBox, narrow ? 3 : 1);
            System.Windows.Controls.Grid.SetRow(TargetLangComboBox, narrow ? 1 : 0);
            System.Windows.Controls.Grid.SetColumn(TargetLangComboBox, narrow ? 0 : 2);
            System.Windows.Controls.Grid.SetColumnSpan(TargetLangComboBox, narrow ? 4 : 1);
            TargetLangComboBox.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(0);
            System.Windows.Controls.Grid.SetColumn(SwapLanguagesButton, narrow ? 3 : 1);
            System.Windows.Controls.Grid.SetRow(LanguageActionsPanel, narrow ? 2 : 0);
            System.Windows.Controls.Grid.SetColumn(LanguageActionsPanel, narrow ? 0 : 3);
            System.Windows.Controls.Grid.SetColumnSpan(LanguageActionsPanel, narrow ? 4 : 1);
            LanguageActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            LanguageActionsPanel.Margin = narrow ? new Thickness(0, 6, 0, 0) : new Thickness(4, 0, 0, 0);
        }
        if (!_isUpdatingPlacement && IsLoaded)
        {
            _presenter?.ReconstrainWindowPlacement();
        }
    }

    internal void BeginPlacementUpdate()
    {
        _isUpdatingPlacement = true;
    }

    internal void EndPlacementUpdate()
    {
        _isUpdatingPlacement = false;
    }

    private void NotifyUserPlacementChanged()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            var bounds = WindowPlacementHelper.GetPhysicalWindowRect(helper.Handle);
            if (bounds.HasValue && !bounds.Value.IsEmpty)
            {
                _presenter?.OnUserChangedPlacement(bounds.Value);
            }
        }
    }
}
