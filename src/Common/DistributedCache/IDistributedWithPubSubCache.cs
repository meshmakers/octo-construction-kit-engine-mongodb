using System.Threading.Tasks;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Interface of a distributed cache with pub sub mechanisms using REDIS
/// </summary>
public interface IDistributedWithPubSubCache
{
    /// <summary>
    ///     Returns true when the channel is connected
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Returns the last message of the given channel as string
    /// </summary>
    /// <param name="channelName">The channel name</param>
    /// <returns></returns>
    Task<TValue?> GetLastMessageAsync<TValue>(string channelName);

    /// <summary>
    ///     Subscribes to a channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <typeparam name="TValue">Type of value in messages</typeparam>
    /// <returns>The channel to access e. g. messages</returns>
    IChannel<TValue> Subscribe<TValue>(string channel);

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    Task PublishAsync<TValue>(string channel, TValue value);
}