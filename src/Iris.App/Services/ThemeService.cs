using System;
using System.Windows;
using Iris.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Iris.App.Services;

/// <summary>
/// 主题管理服务：响应 AppTheme 设置并在 UI 线程上应用 WPF-UI 主题。
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private readonly ISettingsService _settingsService;
    private AppTheme _currentTheme;
    private bool _isSystemEventsSubscribed;
    private bool _disposed;

    public ThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settingsService.Changed += OnSettingsChanged;
    }

    public void Apply(AppTheme theme)
    {
        _currentTheme = theme;

        switch (theme)
        {
            case AppTheme.Light:
                UnsubscribeSystemEvents();
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;

            case AppTheme.Dark:
                UnsubscribeSystemEvents();
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;

            case AppTheme.System:
            default:
                SubscribeSystemEvents();
                ApplySystemTheme();
                break;
        }
    }

    private static void ApplySystemTheme()
    {
        var systemTheme = ApplicationThemeManager.GetSystemTheme();
        var appTheme = systemTheme switch
        {
            SystemTheme.Dark => ApplicationTheme.Dark,
            SystemTheme.Light => ApplicationTheme.Light,
            SystemTheme.Glow => ApplicationTheme.Dark,
            SystemTheme.CapturedMotion => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };

        ApplicationThemeManager.Apply(appTheme);
    }

    private void SubscribeSystemEvents()
    {
        if (!_isSystemEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _isSystemEventsSubscribed = true;
        }
    }

    private void UnsubscribeSystemEvents()
    {
        if (_isSystemEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _isSystemEventsSubscribed = false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_currentTheme == AppTheme.System && !_disposed)
                {
                    ApplySystemTheme();
                }
            });
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.General.Theme != e.NewSettings.General.Theme)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    Apply(e.NewSettings.General.Theme);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.Changed -= OnSettingsChanged;
        UnsubscribeSystemEvents();
    }
}
