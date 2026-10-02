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
    private bool _isUserMovingOrResizing;
    private bool _isUpdatingPlacement;

    public TranslateWindow()
    {
        InitializeComponent();
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
            _isUserMovingOrResizing = true;
            try
            {
                DragMove();
            }
            finally
            {
                _isUserMovingOrResizing = false;
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

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (ViewModel == null || ViewModel.IsPinned || !ViewModel.HideOnDeactivate)
        {
            return;
        }

        if (SourceLangComboBox.IsDropDownOpen || TargetLangComboBox.IsDropDownOpen)
        {
            return;
        }

        _ = _presenter?.OnWindowDeactivated();
    }

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

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

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
        if (!_isUpdatingPlacement && _isUserMovingOrResizing)
        {
            NotifyUserPlacementChanged();
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isUpdatingPlacement && _isUserMovingOrResizing)
        {
            NotifyUserPlacementChanged();
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
