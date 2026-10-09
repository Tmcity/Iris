using System;
using System.Collections.Generic;
using System.Windows;
using Iris.App.Tray;
using Iris.Core.Presentation;
using Iris.Core.Errors;
using Iris.App.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.App.Services;

/// <summary>
/// 托盘通知服务：管理托盘气泡通知展示并在托盘就绪前排队缓存。
/// </summary>
internal sealed partial class TrayNotificationService : INotificationService
{
    private const int MaxPendingCount = 10;
    private static System.Text.CompositeFormat OcrCopiedFormat => System.Text.CompositeFormat.Parse(UiText.OcrCopiedFormat);
    private readonly ILogger<TrayNotificationService> _logger;
    private readonly Lock _gate = new();
    private readonly Queue<PendingNotification> _pendingNotifications = new();
    private TrayIconController? _tray;

    public TrayNotificationService(ILogger<TrayNotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Notify(NotificationLevel level, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        TrayIconController? trayToUse;
        lock (_gate)
        {
            if (_tray == null)
            {
                if (_pendingNotifications.Count >= MaxPendingCount)
                {
                    var dropped = _pendingNotifications.Dequeue();
                    LogNotificationDropped(_logger, dropped.Title, dropped.Message);
                }

                _pendingNotifications.Enqueue(new PendingNotification(level, title, message));
                return;
            }

            trayToUse = _tray;
        }

        DispatchNotification(trayToUse, title, message, level);
    }

    internal void AttachTray(TrayIconController tray)
    {
        ArgumentNullException.ThrowIfNull(tray);

        List<PendingNotification> pendingToFlush;
        lock (_gate)
        {
            _tray = tray;
            pendingToFlush = [.. _pendingNotifications];
            _pendingNotifications.Clear();
        }

        foreach (var item in pendingToFlush)
        {
            DispatchNotification(tray, item.Title, item.Message, item.Level);
        }
    }

    internal bool TryNotifyNow(NotificationLevel level, ErrorCode code)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        TrayIconController? tray;
        lock (_gate) tray = _tray;
        return tray?.TryShowNotificationNow(UiText.AppName, ErrorMessages.GetDefault(code), level) ?? false;
    }

    internal bool TryNotifyScreenshotCopyFailure()
    {
        TrayIconController? tray;
        lock (_gate) tray = _tray;
        return tray?.TryShowNotificationNow(UiText.AppName, UiText.ScreenshotCopyFailed, NotificationLevel.Warning) ?? false;
    }

    internal bool TryNotifyOcrFallback()
    {
        TrayIconController? tray;
        lock (_gate) tray = _tray;
        return tray?.TryShowNotificationNow(UiText.AppName, UiText.OcrFallback, NotificationLevel.Info) ?? false;
    }

    internal bool TryNotifyOcrCopied(int characterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characterCount);
        TrayIconController? tray;
        lock (_gate) tray = _tray;
        return tray?.TryShowNotificationNow(UiText.AppName,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, OcrCopiedFormat, characterCount),
            NotificationLevel.Success) ?? false;
    }

    private static void DispatchNotification(TrayIconController tray, string title, string message, NotificationLevel level)
    {
        var app = Application.Current;
        if (app != null)
        {
            app.Dispatcher.InvokeAsync(() => tray.ShowNotification(title, message, level));
        }
        else
        {
            tray.ShowNotification(title, message, level);
        }
    }

    private sealed record PendingNotification(NotificationLevel Level, string Title, string Message);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "托盘就绪前通知队列已满，丢弃最早一条通知: {Title} - {Message}")]
    private static partial void LogNotificationDropped(ILogger logger, string title, string message);
}
