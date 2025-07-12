using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ScreenTimeTracker.Infrastructure
{
    /// <summary>
    /// Lightweight in-process event bus that supports type-safe pub/sub without any external dependency.
    /// </summary>
    public sealed class ScreenyEventBus
    {
        private static readonly Lazy<ScreenyEventBus> _lazy = new(() => new ScreenyEventBus());
        public static ScreenyEventBus Instance => _lazy.Value;

        private readonly ConcurrentDictionary<Type, List<Delegate>> _subscribers = new();

        private ScreenyEventBus() { }

        /// <summary>
        /// Subscribes to messages of type <typeparamref name="T"/>.
        /// Caller should keep the returned <see cref="IDisposable"/> and dispose it to unsubscribe.
        /// </summary>
        public IDisposable Subscribe<T>(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var list = _subscribers.GetOrAdd(typeof(T), _ => new List<Delegate>());
            lock (list) { list.Add(handler); }
            return new Unsubscriber(() =>
            {
                lock (list) { list.Remove(handler); }
            });
        }

        /// <summary>
        /// Publishes a message to all current subscribers.
        /// </summary>
        public void Publish<T>(T message)
        {
            if (_subscribers.TryGetValue(typeof(T), out var list))
            {
                // Copy to avoid modification during enumeration
                Delegate[] snapshot;
                lock (list) { snapshot = list.ToArray(); }
                foreach (var d in snapshot)
                {
                    if (d is Action<T> action)
                    {
                        try { action(message); } catch { /* swallow */ }
                    }
                }
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly Action _disposeAction;
            private bool _disposed;
            public Unsubscriber(Action disposeAction) => _disposeAction = disposeAction;
            public void Dispose()
            {
                if (_disposed) return;
                _disposeAction();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Marker message indicating the focused window changed.
    /// Payload currently not needed – consumers can query the tracking service.
    /// </summary>
    public readonly struct WindowChangedMessage { }
} 