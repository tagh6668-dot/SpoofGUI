using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows;

namespace Microsoft.UI.Dispatching;

public enum DispatcherQueuePriority
{
    Low = -1,
    Normal = 0,
    High = 1
}

public class DispatcherQueue
{
    private static readonly DispatcherQueue _instance = new();

    public static DispatcherQueue GetForCurrentThread() => _instance;

    public bool TryEnqueue(Action callback)
    {
        if (Application.Current?.Dispatcher is { } d)
        {
            d.BeginInvoke(callback);
            return true;
        }
        callback();
        return true;
    }

    public bool TryEnqueue(DispatcherQueuePriority priority, Action callback)
    {
        return TryEnqueue(callback);
    }
}

public class DispatcherQueueSynchronizationContext : SynchronizationContext
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueSynchronizationContext(DispatcherQueue queue)
    {
        _queue = queue;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.TryEnqueue(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.Invoke(() => d(state));
        }
        else
        {
            d(state);
        }
    }
}

public static class CollectionExtensions
{
    public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue = default!)
    {
        return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public static TValue GetValueOrDefault<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue = default!) where TKey : notnull
    {
        return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }
}
