using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Runtime.Application;

namespace RedoSchool.LevelGeneration.Runtime.Infrastructure
{
    /// <summary>
    /// Minimal <see cref="IRoomEventBus"/> backed by per-type subscriber lists.
    /// Allocation-light: a snapshot copy when dispatching to avoid mutation during iteration.
    /// </summary>
    public sealed class SimpleRoomEventBus : IRoomEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _subscribers = new();

        public void Publish<TEvent>(TEvent evt) where TEvent : struct
        {
            if (!_subscribers.TryGetValue(typeof(TEvent), out List<Delegate> list))
            {
                return;
            }

            // Copy to handle subscribe/unsubscribe during dispatch.
            Delegate[] snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                ((Action<TEvent>)snapshot[i]).Invoke(evt);
            }
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!_subscribers.TryGetValue(typeof(TEvent), out List<Delegate> list))
            {
                list = new List<Delegate>(2);
                _subscribers[typeof(TEvent)] = list;
            }

            list.Add(handler);
            return new Subscription(() =>
            {
                if (_subscribers.TryGetValue(typeof(TEvent), out List<Delegate> l))
                {
                    l.Remove(handler);
                }
            });
        }

        private sealed class Subscription : IDisposable
        {
            private Action _onDispose;

            public Subscription(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                _onDispose?.Invoke();
                _onDispose = null;
            }
        }
    }
}
