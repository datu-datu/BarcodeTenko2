using System;
using System.Windows;
using System.Windows.Threading;

namespace Tenko.Native.Services
{
    public class ClockService : IClockService
    {
        private DispatcherTimer? _timer;

        public event Action<DateTime>? OnTick;

        public DateTime Now => DateTime.Now;

        public ClockService()
        {
            if (Application.Current?.Dispatcher != null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _timer.Tick += (s, e) => OnTick?.Invoke(DateTime.Now);
                _timer.Start();
            }
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer = null;
        }
    }
}
