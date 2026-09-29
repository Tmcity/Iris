using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Resources;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Microsoft.Extensions.Logging;

namespace Iris.App.Hosting;

/// <summary>
/// 统一动作分发器（App 层实现），负责防重入、UI 调度、异常安全边界及通知分发。
/// 规则见 ARCH §6.1 与 CCR-007。
/// </summary>
internal sealed partial class ActionDispatcher : IActionDispatcher
{
    private readonly Dictionary<IrisAction, IActionHandler> _handlers;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly INotificationService _notificationService;
    private readonly ILogger<ActionDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _gate = new();
    private readonly HashSet<IrisAction> _executingActions = [];

    public ActionDispatcher(
        IEnumerable<IActionHandler> handlers,
        IUiDispatcher uiDispatcher,
        INotificationService notificationService,
        ILogger<ActionDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var map = new Dictionary<IrisAction, IActionHandler>();
        foreach (var handler in handlers)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handlers), "Handler collection contains a null item.");
            }

            if (!Enum.IsDefined(handler.Action))
            {
                throw new ArgumentException($"Handler registered with undefined action {(int)handler.Action}.", nameof(handlers));
            }

            if (!map.TryAdd(handler.Action, handler))
            {
                throw new ArgumentException($"Duplicate handler registered for action {handler.Action}.", nameof(handlers));
            }
        }

        _handlers = map;
    }

    public async Task DispatchAsync(IrisAction action, ActionContext context, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (context == null || !Enum.IsDefined(action))
        {
            LogInvalidArgument(_logger, (int)action);
            SafeNotify(NotificationLevel.Error, AppResources.AppName, ErrorMessages.GetDefault(ErrorCode.InvalidArgument));
            return;
        }

        if (!_handlers.TryGetValue(action, out var handler))
        {
            LogActionNotRegistered(_logger, action);
            SafeNotify(NotificationLevel.Warning, AppResources.AppName, AppResources.ActionNotAvailable);
            return;
        }

        if (!TryAcquireExecution(action))
        {
            return;
        }

        try
        {
            await _uiDispatcher.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await handler.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 业务取消或调度器关闭静默结束，不弹错误通知
        }
        catch (IrisException ex)
        {
            LogActionFailed(_logger, action, context.Trigger, ex.Code, ex.GetType().FullName ?? ex.GetType().Name);
            SafeNotify(NotificationLevel.Error, AppResources.AppName, ErrorMessages.GetDefault(ex.Code));
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, action, context.Trigger, ErrorCode.Unknown, ex.GetType().FullName ?? ex.GetType().Name);
            SafeNotify(NotificationLevel.Error, AppResources.AppName, ErrorMessages.GetDefault(ErrorCode.Unknown));
        }
        finally
        {
            ReleaseExecution(action);
        }
    }

    private static bool IsScreenshotAction(IrisAction action) =>
        action is IrisAction.ScreenshotTranslate
            or IrisAction.ScreenshotOcr
            or IrisAction.SilentScreenshotOcr;

    private bool TryAcquireExecution(IrisAction action)
    {
        lock (_gate)
        {
            if (IsScreenshotAction(action))
            {
                if (_executingActions.Contains(IrisAction.ScreenshotTranslate) ||
                    _executingActions.Contains(IrisAction.ScreenshotOcr) ||
                    _executingActions.Contains(IrisAction.SilentScreenshotOcr))
                {
                    return false;
                }
            }
            else
            {
                if (_executingActions.Contains(action))
                {
                    return false;
                }
            }

            _executingActions.Add(action);
            return true;
        }
    }

    private void ReleaseExecution(IrisAction action)
    {
        lock (_gate)
        {
            _executingActions.Remove(action);
        }
    }

    private void SafeNotify(NotificationLevel level, string title, string message)
    {
        try
        {
            _notificationService.Notify(level, title, message);
        }
        catch (Exception ex)
        {
            LogNotificationFailed(_logger, ex.GetType().FullName ?? ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "无效的动作调用参数: Action={Action}")]
    private static partial void LogInvalidArgument(ILogger logger, int action);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "动作尚未注册处理程序: Action={Action}")]
    private static partial void LogActionNotRegistered(ILogger logger, IrisAction action);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "动作执行失败: Action={Action}, Trigger={Trigger}, ErrorCode={ErrorCode}, ExceptionType={ExceptionType}")]
    private static partial void LogActionFailed(ILogger logger, IrisAction action, ActionTrigger trigger, ErrorCode errorCode, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "通知服务抛出异常，未能展示通知: ExceptionType={ExceptionType}")]
    private static partial void LogNotificationFailed(ILogger logger, string exceptionType);
}
