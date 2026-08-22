using System;

namespace Tenko.Native.Services
{
    public interface IClockService : IDisposable
    {
        event Action<DateTime>? OnTick;
        DateTime Now { get; }
    }
}
