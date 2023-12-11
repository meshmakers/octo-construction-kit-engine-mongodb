using Meshmakers.Octo.Common.DistributedCache.Channels;
using Meshmakers.Octo.Common.DistributedCache.Payloads;

namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Interface of a distributed cache with pub sub mechanisms using REDIS
/// </summary>
public interface IDistributedCache
{
    /// <summary>
    ///     Returns true when the channel is connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     Returns the last message of the given event
    /// </summary>
    /// <param name="eventName">Name of event</param>
    /// <returns></returns>
    Task<TValue?> GetLastEventAsync<TValue>(string eventName);

    /// <summary>
    ///     Subscribes to an event channel
    /// </summary>
    /// <param name="eventName">The name of the event</param>
    /// <typeparam name="TValue">Type of value in messages</typeparam>
    /// <returns>The channel to access e. g. messages</returns>
    Task<IEventChannel<TValue>> SubscribeEventAsync<TValue>(string eventName);

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="eventName">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    Task TriggerEventAsync<TValue>(string eventName, TValue value);

    /// <summary>
    /// Subscribes to an operation invoke channel
    /// </summary>
    /// <param name="operationName">The name of operation</param>
    /// <typeparam name="TInvoke">The type of invoke arguments</typeparam>
    /// <typeparam name="TResult">The type of result arguments</typeparam>
    /// <returns></returns>
    Task<IOperationInvokeChannel<TInvoke, TResult>> SubscribeInvokeOperationAsync<TInvoke, TResult>(string operationName);


    /// <summary>
    /// Invokes an operation and returns the possibility to subscribe to the result.
    /// </summary>
    /// <param name="operationName">The name of operation</param>
    /// <param name="arguments">Argument of the operation</param>
    /// <typeparam name="TInvoke">The type of invoke arguments</typeparam>
    /// <typeparam name="TError">The error type when operation failed</typeparam>
    /// <typeparam name="TResult">The result type when operation succeeded</typeparam>
    /// <returns></returns>
    Task<IOperationResultChannel<TError, TResult>> InvokeOperationAsync<TInvoke, TError, TResult>(string operationName, TInvoke arguments);
    
    
    /// <summary>
    ///     Caches a stream for the given amount of time
    /// </summary>
    /// <param name="cacheStreamKey">The key identifying the stream</param>
    /// <param name="value">The byte array</param>
    /// <param name="contentType">Content type of the file</param>
    /// <param name="expiry">The amount of time the stream gets cached</param>
    /// <returns></returns>
    Task CacheStreamAsync(string cacheStreamKey, byte[] value, string contentType, TimeSpan? expiry = null);

    /// <summary>
    ///     Deletes a cached stream
    /// </summary>
    /// <param name="cacheStreamKey">The key identifying the stream</param>
    /// <returns></returns>
    Task DeleteCacheStreamAsync(string cacheStreamKey);

    /// <summary>
    ///     Retrieves a cached stream
    /// </summary>
    /// <param name="cacheStreamKey">The key identifying the stream</param>
    /// <returns></returns>
    Task<CacheStream?> GetCacheStreamAsync(string cacheStreamKey);
}