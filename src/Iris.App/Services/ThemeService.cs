using System;
using System.Threading;
using System.Windows;
using Iris.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Iris.App.Services;

/// <summary>
/// 主题管理服务：响应 AppTheme 设置与窗口会话即时预览，并在 UI 线程上应用 WPF-UI 主题。
/// 完整规则见 SETTINGS_EDITING.md §5。
/// </summary>
internal sealed partial class ThemeService : IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<ThemeService>? _logger;
    private readonly Action<ApplicationTheme>? _visualThemeApplier;
    private readonly WindowMaterialService? _windowMaterialService;
    private readonly Lock _gate = new();

    private object? _previewOwner;
    private AppTheme? _previewTheme;
    private AppTheme _currentTheme;
    private bool _isSystemEventsSubscribed;
    private bool _disposed;

    public ThemeService(
        ISettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<ThemeService>? logger = null,
        Action<ApplicationTheme>? visualThemeApplier = null,
        WindowMaterialService? windowMaterialService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger;
        _visualThemeApplier = visualThemeApplier;
        _windowMaterialService = windowMaterialService;
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

    internal bool HasWindowMaterialService => _windowMaterialService is not null;

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

    private void SafeApplyWpfUiTheme(ApplicationTheme theme)
    {
        if (_visualThemeApplier != null)
        {
            try
            {
                _visualThemeApplier(theme);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
                }
            }
        }
        else
        {
            try
            {
                if (Application.Current != null)
                {
                    // Ask for "no backdrop" explicitly: the window backdrop is owned by
                    // WindowMaterialService, and passing Mica here would replace its acrylic.
                    ApplicationThemeManager.Apply(theme, WindowBackdropType.None);
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
                }
            }
        }

        // Outside the seam branch on purpose: the Iris dictionary and the window material have to
        // follow the theme on the test path too. Each step is guarded like the calls above, so a
        // failure stays a logged warning: AppBootstrapper applies an explicit Light/Dark theme
        // synchronously during startup, with no catch of its own.
        try
        {
            ApplyIrisThemeDictionary(theme);
            ApplyIrisSolidFallbackDictionary(theme, _windowMaterialService?.IsAcrylicSupported ?? true);
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
            }
        }

        try
        {
            _windowMaterialService?.RefreshOpenWindows();
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
            }
        }
    }

    private static readonly string[] IrisThemeDictionaryNames = ["Iris.Light.xaml", "Iris.Dark.xaml"];

    /// <summary>
    /// Swaps the Iris-owned colour dictionary for the one that matches the theme. WPF-UI swaps
    /// its own theme dictionary in ApplicationThemeManager; this one is ours, so it has to follow.
    /// </summary>
    internal static void ApplyIrisThemeDictionary(ApplicationTheme theme)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return;
        }

        var target = new ResourceDictionary
        {
            Source = new Uri(
                theme == ApplicationTheme.Dark
                    ? "pack://application:,,,/Iris.App;component/Themes/Iris.Dark.xaml"
                    : "pack://application:,,,/Iris.App;component/Themes/Iris.Light.xaml",
                UriKind.Absolute),
        };

        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source is not null && IrisThemeDictionaryNames.Any(name => source.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries[index] = target;
                return;
            }
        }

        dictionaries.Add(target);
    }

    private static readonly string[] IrisSolidFallbackDictionaryNames = ["Iris.SolidFallback.xaml", "Iris.SolidFallback.Dark.xaml"];

    /// <summary>
    /// Merges the opaque Iris colour dictionary when the system has no acrylic backdrop, and
    /// removes it when it does. The dictionary goes right after the Iris theme dictionary: later
    /// entries in MergedDictionaries win, so the opaque values override the translucent ones.
    /// </summary>
    internal static void ApplyIrisSolidFallbackDictionary(ApplicationTheme theme, bool acrylicSupported)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return;
        }

        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (SourceEndsWithAny(dictionaries[index], IrisSolidFallbackDictionaryNames))
            {
                dictionaries.RemoveAt(index);
            }
        }

        if (acrylicSupported)
        {
            return;
        }

        var fallback = new ResourceDictionary
        {
            Source = new Uri(
                theme == ApplicationTheme.Dark
                    ? "pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.Dark.xaml"
                    : "pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml",
                UriKind.Absolute),
        };

        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (SourceEndsWithAny(dictionaries[index], IrisThemeDictionaryNames))
            {
                dictionaries.Insert(index + 1, fallback);
                return;
            }
        }

        dictionaries.Add(fallback);
    }

    private static bool SourceEndsWithAny(ResourceDictionary dictionary, string[] names)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null && names.Any(name => source.EndsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    private void SafeApplySystemTheme()
    {
        try
        {
            var systemTheme = Application.Current != null ? ApplicationThemeManager.GetSystemTheme() : SystemTheme.Light;
            var appTheme = systemTheme switch
            {
                SystemTheme.Dark => ApplicationTheme.Dark,
                SystemTheme.Light => ApplicationTheme.Light,
                SystemTheme.Glow => ApplicationTheme.Dark,
                SystemTheme.CapturedMotion => ApplicationTheme.Dark,
                _ => ApplicationTheme.Light,
            };

            SafeApplyWpfUiTheme(appTheme);
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                LogThemeApplyFailed(_logger, "System", ex.GetType().FullName ?? ex.GetType().Name);
            }
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

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 1, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "应用视觉主题 '{ThemeName}' 失败，异常类型: '{ErrorType}'")]
    private static partial void LogThemeApplyFailed(Microsoft.Extensions.Logging.ILogger logger, string themeName, string errorType);
}
