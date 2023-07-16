using System;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.Shared.DistributedCache;

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
    
    
    /// <summary>
    /// Caches a stream for the given amount of time
    /// </summary>
    /// <param name="key">The key identifying the stream</param>
    /// <param name="value">The byte array</param>
    /// <param name="contentType">Content type of the file</param>
    /// <param name="expiry">The amount of time the stream gets cached</param>
    /// <returns></returns>
    Task CacheStreamAsync(string key, byte[] value, string contentType, TimeSpan? expiry = null);

    /// <summary>
    /// Deletes a cached stream
    /// </summary>
    /// <param name="key">The key identifying the stream</param>
    /// <returns></returns>
    Task DeleteCacheStreamAsync(string key);

    /// <summary>
    /// Retrieves a cached stream
    /// </summary>
    /// <param name="key">The key identifying the stream</param>
    /// <returns></returns>
    Task<CacheStream?> GetCacheStreamAsync(string key);
}