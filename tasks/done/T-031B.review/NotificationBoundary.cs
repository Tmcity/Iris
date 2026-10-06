using System.Collections.Concurrent;
using Iris.App.Tray;
using Iris.Core.Presentation;

sealed class NotificationBoundary:ITrayNotificationNative
{
    public bool IsCreated {get;set;}=true;
    internal readonly ConcurrentQueue<(string Title,string Message,NotificationLevel Level)> Shown=new();
    internal Action? BeforeShow {get;set;}
    public void Show(string title,string message,NotificationLevel level)
    {BeforeShow?.Invoke();Shown.Enqueue((title,message,level));}
}
