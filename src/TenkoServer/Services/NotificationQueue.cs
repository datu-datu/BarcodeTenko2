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
        public string StudentName { get; set; } = string.Empty;
        public string StudentCode { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string ToEmail { get; set; } = string.Empty;
    }

    public interface INotificationQueue
    {
        ValueTask QueueNotificationAsync(NotificationTask task, CancellationToken cancellationToken = default);
        ValueTask<NotificationTask> DequeueNotificationAsync(CancellationToken cancellationToken);
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
            await _queue.Writer.WriteAsync(task, cancellationToken);
        }

        public async ValueTask<NotificationTask> DequeueNotificationAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
