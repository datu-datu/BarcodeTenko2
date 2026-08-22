using System;

namespace Tenko.Lite.Services
{
    public interface IClockService : IDisposable
    {
        event Action<DateTime>? OnTick;
        DateTime Now { get; }
    }
}
