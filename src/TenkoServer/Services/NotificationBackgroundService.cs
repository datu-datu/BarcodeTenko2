using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenkoServer.Data;
using TenkoServer.Data.Models;
using TenkoServer.Models;

namespace TenkoServer.Services
{
    public class NotificationBackgroundService : BackgroundService
    {
        private readonly INotificationQueue _queue;
        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<TenkoServerOptions> _options;
        private readonly ILogger<NotificationBackgroundService> _logger;

        public NotificationBackgroundService(
            INotificationQueue queue,
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<TenkoServerOptions> options,
            ILogger<NotificationBackgroundService> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _httpClientFactory = httpClientFactory;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("NotificationBackgroundService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var task = await _queue.DequeueNotificationAsync(stoppingToken);
                    await ProcessNotificationAsync(task, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing notification from queue.");
                }
            }

            _logger.LogInformation("NotificationBackgroundService stopped.");
        }

        private async Task ProcessNotificationAsync(NotificationTask task, CancellationToken cancellationToken)
        {
            var options = _options.CurrentValue;
            if (!options.EnableNotifications || string.IsNullOrWhiteSpace(options.PowerAutomateWebhookUrl))
            {
                _logger.LogDebug("Notification skipped for {Email} (PowerAutomateWebhookUrl not configured or notifications disabled).", task.ToEmail);
                return;
            }

            var payload = new
            {
                to = task.ToEmail,
                studentNumber = task.StudentNumber,
                studentName = task.StudentName,
                studentCode = task.StudentCode,
                location = task.Location,
                timestamp = task.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                clientId = task.ClientId
            };

            bool isSuccess = false;
            int statusCode = 0;
            string? errorMessage = null;

            try
            {
                var client = _httpClientFactory.CreateClient("PowerAutomateClient");
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.PostAsJsonAsync(options.PowerAutomateWebhookUrl, payload, cancellationToken);
                statusCode = (int)response.StatusCode;
                isSuccess = response.IsSuccessStatusCode;

                if (!isSuccess)
                {
                    errorMessage = $"HTTP {statusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
                    _logger.LogWarning("Power Automate webhook failed for {Email}: {Error}", task.ToEmail, errorMessage);
                }
                else
                {
                    _logger.LogInformation("Successfully sent notification for student {StudentNumber} ({Email}) to Power Automate.", task.StudentNumber, task.ToEmail);
                }
            }
            catch (Exception ex)
            {
                isSuccess = false;
                errorMessage = ex.Message;
                _logger.LogError(ex, "Exception posting to Power Automate webhook for {Email}", task.ToEmail);
            }

            // DBに送信ログを記録
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TenkoDbContext>();

                var log = new NotificationLog
                {
                    ScanId = task.ScanId,
                    StudentNumber = task.StudentNumber,
                    ToEmail = task.ToEmail,
                    SentAt = DateTime.UtcNow,
                    IsSuccess = isSuccess,
                    StatusCode = statusCode,
                    ErrorMessage = errorMessage
                };

                db.NotificationLogs.Add(log);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save NotificationLog to database.");
            }
        }
    }
}
