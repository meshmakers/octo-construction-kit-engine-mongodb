using System;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Backend.DistributedCache;

/// <summary>
///     Interface of a channel
/// </summary>
/// <typeparam name="TValue">Type of value in messages</typeparam>
public interface IChannel<TValue> : IDisposable where TValue : IConvertible
{
    /// <summary>
    ///     Adds a callback to receive messages
    /// </summary>
    /// <param name="action">The action that is performed</param>
    void OnMessage(Func<ChannelMessage<TValue>, Task> action);

    /// <summary>
    ///     Unsubscribe the channel
    /// </summary>
    /// <returns>Task</returns>
    Task UnsubscribeAsync();
}
