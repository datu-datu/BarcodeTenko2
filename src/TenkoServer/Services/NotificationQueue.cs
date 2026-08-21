using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TenkoServer.Services
{
    public class NotificationTask
    {
        public string ScanId { get; set; } = string.Empty;
        public ushort StudentNumber { get; set; }
        public string Location { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }

        /// <summary>キューに投入された時刻 (UTC)。バッチ送信の待機時間計算に使用する</summary>
        public DateTime EnqueuedAtUtc { get; set; }
    }

    public interface INotificationQueue
    {
        ValueTask QueueNotificationAsync(NotificationTask task, CancellationToken cancellationToken = default);
        ValueTask<NotificationTask> DequeueNotificationAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 指定したタイムアウト时间内に新しい通知タスクが到着すれば取り出す。
        /// タイムアウトした場合は null を返す（バッチ集約送信用）。
        /// </summary>
        ValueTask<NotificationTask?> TryDequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    }

    public class NotificationQueue : INotificationQueue
    {
        private readonly Channel<NotificationTask> _queue;

        public NotificationQueue()
        {
            var options = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _queue = Channel.CreateBounded<NotificationTask>(options);
        }

        public async ValueTask QueueNotificationAsync(NotificationTask task, CancellationToken cancellationToken = default)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            task.EnqueuedAtUtc = DateTime.UtcNow;
            await _queue.Writer.WriteAsync(task, cancellationToken);
        }

        public async ValueTask<NotificationTask> DequeueNotificationAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }

        public async ValueTask<NotificationTask?> TryDequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                return _queue.Reader.TryRead(out var immediate) ? immediate : null;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(cts.Token))
                {
                    return null;
                }

                return _queue.Reader.TryRead(out var task) ? task : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // タイムアウト: バッチ送信のタイミング
                return null;
            }
        }
    }
}
