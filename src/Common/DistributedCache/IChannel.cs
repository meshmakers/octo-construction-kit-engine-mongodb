using System;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Interface of a channel
/// </summary>
/// <typeparam name="TValue">Type of value in messages</typeparam>
public interface IChannel<out TValue> : IDisposable
{
    /// <summary>
    ///     Adds a callback to receive messages
    /// </summary>
    /// <param name="action">The action that is performed</param>
    void OnMessage(Func<IChannelMessage<TValue>, Task> action);

    /// <summary>
    ///     Unsubscribe the channel
    /// </summary>
    /// <returns>Task</returns>
    Task UnsubscribeAsync();
}
