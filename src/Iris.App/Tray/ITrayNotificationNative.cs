using Iris.Core.Presentation;

namespace Iris.App.Tray;

// Optional external shell boundary for isolated tests; production uses its TaskbarIcon.
internal interface ITrayNotificationNative
{
    bool IsCreated { get; }
    void Show(string title, string message, NotificationLevel level);
}
