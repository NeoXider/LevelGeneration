using System;

namespace Neoxider.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Mission-scoped internal event bus for runtime room events (Entered / Completed / DoorStateChanged).
    /// Intentionally NOT MessagePipe — scope is one mission, no cross-feature delivery.
    /// </summary>
    public interface IRoomEventBus
    {
        public void Publish<TEvent>(TEvent evt) where TEvent : struct;
        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
    }
}
