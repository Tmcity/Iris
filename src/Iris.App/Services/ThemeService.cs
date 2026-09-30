using System;
using System.Threading;
using System.Windows;
using Iris.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Iris.App.Services;

/// <summary>
/// 主题管理服务：响应 AppTheme 设置与窗口会话即时预览，并在 UI 线程上应用 WPF-UI 主题。
/// 完整规则见 SETTINGS_EDITING.md §5。
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly Lock _gate = new();

    private object? _previewOwner;
    private AppTheme? _previewTheme;
    private AppTheme _currentTheme;
    private bool _isSystemEventsSubscribed;
    private bool _disposed;

    public ThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settingsService.Changed += OnSettingsChanged;
    }

    internal AppTheme EffectiveTheme
    {
        get
        {
            lock (_gate)
            {
                return _previewTheme ?? _settingsService.Current.General.Theme;
            }
        }
    }

    internal bool HasActivePreview
    {
        get
        {
            lock (_gate)
            {
                return _previewTheme.HasValue;
            }
        }
    }

    internal object? PreviewOwner
    {
        get
        {
            lock (_gate)
            {
                return _previewOwner;
            }
        }
    }

    public void Apply(AppTheme theme)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        ApplyEffectiveTheme();
    }

    public void SetPreview(object sessionOwner, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(sessionOwner);

        lock (_gate)
        {
            if (_disposed) return;
            _previewOwner = sessionOwner;
            _previewTheme = theme;
        }

        ApplyEffectiveTheme();
    }

    public void ClearPreview(object sessionOwner)
    {
        ArgumentNullException.ThrowIfNull(sessionOwner);

        lock (_gate)
        {
            if (_disposed) return;
            if (!ReferenceEquals(_previewOwner, sessionOwner))
            {
                // 旧会话无权清除新会话预览
                return;
            }

            _previewOwner = null;
            _previewTheme = null;
        }

        ApplyEffectiveTheme();
    }

    private void ApplyEffectiveTheme()
    {
        void ExecuteApply()
        {
            AppTheme themeToApply;
            lock (_gate)
            {
                if (_disposed) return;
                themeToApply = _previewTheme ?? _settingsService.Current.General.Theme;
            }

            ApplyCore(themeToApply);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(ExecuteApply);
        }
        else
        {
            ExecuteApply();
        }
    }

    private void ApplyCore(AppTheme theme)
    {
        _currentTheme = theme;

        switch (theme)
        {
            case AppTheme.Light:
                UnsubscribeSystemEvents();
                SafeApplyWpfUiTheme(ApplicationTheme.Light);
                break;

            case AppTheme.Dark:
                UnsubscribeSystemEvents();
                SafeApplyWpfUiTheme(ApplicationTheme.Dark);
                break;

            case AppTheme.System:
            default:
                SubscribeSystemEvents();
                SafeApplySystemTheme();
                break;
        }
    }

    private static void SafeApplyWpfUiTheme(ApplicationTheme theme)
    {
        try
        {
            if (Application.Current != null)
            {
                ApplicationThemeManager.Apply(theme);
            }
        }
        catch
        {
            // Headless / 非 WPF 环境容错
        }
    }

    private static void SafeApplySystemTheme()
    {
        try
        {
            if (Application.Current != null)
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
        }
        catch
        {
            // Headless / 非 WPF 环境容错
        }
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
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(() =>
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                        if (EffectiveTheme != AppTheme.System) return;
                    }
                    SafeApplySystemTheme();
                });
            }
            else
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    if (EffectiveTheme != AppTheme.System) return;
                }
                SafeApplySystemTheme();
            }
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.General.Theme != e.NewSettings.General.Theme)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(() =>
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                    }
                    ApplyEffectiveTheme();
                });
            }
            else
            {
                lock (_gate)
                {
                    if (_disposed) return;
                }
                ApplyEffectiveTheme();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _previewOwner = null;
            _previewTheme = null;
            _settingsService.Changed -= OnSettingsChanged;
            UnsubscribeSystemEvents();
        }
    }
}
