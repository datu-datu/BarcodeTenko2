using System;
using Microsoft.Extensions.Options;
using TenkoServer.Models;

namespace TenkoServer.Services
{
    public interface INotificationStateService
    {
        bool IsAutoSendEnabled { get; set; }
        bool IsWebhookConfigured { get; }
    }

    public class NotificationStateService : INotificationStateService
    {
        private readonly IOptionsMonitor<TenkoServerOptions> _options;
        private bool _isAutoSendEnabled = true;

        public NotificationStateService(IOptionsMonitor<TenkoServerOptions> options)
        {
            _options = options;
            _isAutoSendEnabled = options.CurrentValue.EnableNotifications;
        }

        public bool IsAutoSendEnabled
        {
            get => _isAutoSendEnabled;
            set => _isAutoSendEnabled = value;
        }

        public bool IsWebhookConfigured => !string.IsNullOrWhiteSpace(_options.CurrentValue.PowerAutomateWebhookUrl);
    }
}
