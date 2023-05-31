using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Meshmakers.Octo.Backend.DistributedCache;

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
    ///     Returns the Redis database
    /// </summary>
    IDatabase Database { get; }

    /// <summary>
    /// Returns the last message of the given channel as string
    /// </summary>
    /// <param name="channelName">The channel name</param>
    /// <returns></returns>
    Task<string?> GetLastMessageAsStringAsync(string channelName);

    /// <summary>
    ///     Subscribes to a channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <typeparam name="TValue">Type of value in messages</typeparam>
    /// <returns>The channel to access e. g. messages</returns>
    IChannel<TValue> Subscribe<TValue>(string channel) where TValue : IConvertible;

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    Task PublishAsync(string channel, string value);

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    Task PublishAsync(string channel, int value);

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    Task PublishAsync(string channel, long value);

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    void PublishAsync(string channel, double value);
}
