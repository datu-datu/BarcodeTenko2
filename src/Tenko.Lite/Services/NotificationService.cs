using System;

namespace Tenko.Lite.Services
{
    public enum NotificationType
    {
        Success,
        Warning,
        Error
    }

    public class NotificationEventArgs : EventArgs
    {
        public string Message { get; }
        public NotificationType Type { get; }

        public NotificationEventArgs(string message, NotificationType type)
        {
            Message = message;
            Type = type;
        }
    }

    public class NotificationService
    {
        public event EventHandler<NotificationEventArgs>? OnNotification;

        public void Success(string message) => OnNotification?.Invoke(this, new NotificationEventArgs(message, NotificationType.Success));
        public void Warning(string message) => OnNotification?.Invoke(this, new NotificationEventArgs(message, NotificationType.Warning));
        public void Error(string message) => OnNotification?.Invoke(this, new NotificationEventArgs(message, NotificationType.Error));
    }
}
