using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>
    /// 通知キューからタスクを取り出し、Power Automate Webhook へバッチ集約送信する。
    /// Teams Webhook / Power Automate の 1分あたり受信制限を回避するため、
    /// 「最大 MaxNotificationsPerBatch 件」または「最後のスキャンから NotificationBatchWindowSeconds 秒経過」
    /// を条件に複数件を 1 通の JSON 配列として POST する。
    /// また個人情報保護のため、送信ペイロードは studentNumber / location / timestamp のみとする
    /// （氏名・メールアドレス等は Power Automate 側で M365 テナント情報から解決する）。
    /// </summary>
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
                    var first = await _queue.DequeueNotificationAsync(stoppingToken);
                    List<NotificationTask> batch = await CollectBatchAsync(first, stoppingToken);
                    await ProcessBatchAsync(batch, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing notifications from queue.");
                }
            }

            _logger.LogInformation("NotificationBackgroundService stopped.");
        }

        /// <summary>
        /// 最初のタスクを起点に、「最大件数」または「最後のスキャンからの経過秒」に達するまでキューから集約する
        /// </summary>
        private async Task<List<NotificationTask>> CollectBatchAsync(NotificationTask first, CancellationToken cancellationToken)
        {
            var options = _options.CurrentValue;
            int maxBatchSize = Math.Max(1, options.MaxNotificationsPerBatch);
            var batch = new List<NotificationTask> { first };

            while (batch.Count < maxBatchSize && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan remainingWindow = GetRemainingWindow(batch[batch.Count - 1], options);
                if (remainingWindow <= TimeSpan.Zero)
                {
                    break;
                }

                NotificationTask? next = await _queue.TryDequeueAsync(remainingWindow, cancellationToken);
                if (next == null)
                {
                    break;
                }

                batch.Add(next);
            }

            return batch;
        }

        private static TimeSpan GetRemainingWindow(NotificationTask lastEnqueued, TenkoServerOptions options)
        {
            int windowSeconds = Math.Max(0, options.NotificationBatchWindowSeconds);
            TimeSpan elapsedSinceLastScan = DateTime.UtcNow - lastEnqueued.EnqueuedAtUtc;
            return TimeSpan.FromSeconds(windowSeconds) - elapsedSinceLastScan;
        }

        private async Task ProcessBatchAsync(List<NotificationTask> batch, CancellationToken cancellationToken)
        {
            var options = _options.CurrentValue;
            if (!options.EnableNotifications || string.IsNullOrWhiteSpace(options.PowerAutomateWebhookUrl))
            {
                _logger.LogDebug("{Count} notifications skipped (PowerAutomateWebhookUrl not configured or notifications disabled).", batch.Count);
                return;
            }

            // 個人情報（宛先メールアドレス・氏名・出席番号・端末ID）は送信しない。
            // Power Automate 側で学籍番号から M365 テナント情報を動的に解決する。
            var payload = batch.Select(t => new
            {
                studentNumber = t.StudentNumber,
                location = t.Location,
                timestamp = t.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
            });

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
                    _logger.LogWarning("Power Automate webhook failed for batch of {Count} notifications: {Error}", batch.Count, errorMessage);
                }
                else
                {
                    _logger.LogInformation("Successfully sent batch of {Count} notifications ({StudentNumbers}) to Power Automate.",
                        batch.Count, string.Join(",", batch.Select(t => t.StudentNumber)));
                }
            }
            catch (Exception ex)
            {
                isSuccess = false;
                errorMessage = ex.Message;
                _logger.LogError(ex, "Exception posting batch of {Count} notifications to Power Automate webhook.", batch.Count);
            }

            // DBに送信ログを記録
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TenkoDbContext>();

                foreach (NotificationTask task in batch)
                {
                    db.NotificationLogs.Add(new NotificationLog
                    {
                        ScanId = task.ScanId,
                        StudentNumber = task.StudentNumber,
                        SentAt = DateTime.UtcNow,
                        IsSuccess = isSuccess,
                        StatusCode = statusCode,
                        ErrorMessage = errorMessage
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save NotificationLogs to database.");
            }
        }
    }
}
