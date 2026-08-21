using System;

namespace Tenko.Native.Services
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

        // 通知本文と通知種別をまとめてイベント購読側へ渡す。
        public NotificationEventArgs(string message, NotificationType type)
        {
            Message = message;
            Type = type;
        }
    }

    public class NotificationService
    {
        public event EventHandler<NotificationEventArgs>? OnNotification;

        // 成功通知を発行する。
        public void Success(string message) => OnNotification?.Invoke(this, new NotificationEventArgs(message, NotificationType.Success));

        // 注意通知を発行する。
        public void Warning(string message) => OnNotification?.Invoke(this, new NotificationEventArgs(message, NotificationType.Warning));

        // エラー通知を発行する。
        public void Error(string message) => OnNotification?.Invoke(this, new NotificationEventArgs(message, NotificationType.Error));
    }
}
