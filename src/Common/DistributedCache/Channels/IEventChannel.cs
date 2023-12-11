namespace Meshmakers.Octo.Common.DistributedCache.Channels;

/// <summary>
///     Interface of an event
/// </summary>
/// <typeparam name="TValue">Type of event</typeparam>
public interface IEventChannel<out TValue> : IDisposable
{
    /// <summary>
    ///     Adds a callback to receive events
    /// </summary>
    /// <param name="action">The action that is performed when event is raised.</param>
    void OnEvent(Func<TValue?, Task> action);

    /// <summary>
    ///     Unsubscribe the event
    /// </summary>
    /// <returns>Task</returns>
    Task UnsubscribeAsync();
}