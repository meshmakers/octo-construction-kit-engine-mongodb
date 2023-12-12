using System.Diagnostics;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributedCache.Channels;
using Meshmakers.Octo.Common.DistributedCache.Configuration.Options;
using Meshmakers.Octo.Common.DistributedCache.Payloads;
using Meshmakers.Octo.Common.Shared;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Implements a distributed cache with pub sub mechanisms using REDIS
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class DistributedCache : IDistributedCache
{
    private readonly string _currentClientName;
    private readonly IDatabase _database;
    private readonly ConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;

    /// <summary>
    ///     Constructor
    /// </summary>
    public DistributedCache(IOptions<DistributeCacheWithPubSubOptions> options)
    {
        var connectionString = options.Value.Host;
        if (!string.IsNullOrWhiteSpace(options.Value.Password)) connectionString += $",password={options.Value.Password}";

        _redis = ConnectionMultiplexer.Connect(connectionString);
        _database = _redis.GetDatabase();
        _subscriber = _redis.GetSubscriber();
        
        _currentClientName = $"{_redis.ClientName}_{Process.GetCurrentProcess().Id}";
    }

    /// <inheritdoc />
    public async Task<TValue?> GetLastEventAsync<TValue>(string eventName)
    {
        var lastMessage = await _database.ListGetByIndexAsync($"{CacheCommon.LastEventPrefix}{eventName}", -1);
        if (string.IsNullOrWhiteSpace(lastMessage))
        {
            return default;
        }
        return lastMessage.ToString().Deserialize<TValue>();
    }

    /// <inheritdoc />
    public async Task<IEventChannel<TValue>> SubscribeEventAsync<TValue>(string eventName)
    {
        return new EventChannel<TValue>(_currentClientName, await _subscriber.SubscribeAsync(
            RedisChannel.Literal(CacheCommon.EventPrefix + eventName)));
    }
    
    /// <inheritdoc />
    public IEventChannel<TValue> SubscribeEvent<TValue>(string eventName)
    {
        return new EventChannel<TValue>(_currentClientName, _subscriber.Subscribe(
            RedisChannel.Literal(CacheCommon.EventPrefix + eventName)));
    }

    /// <inheritdoc />
    public async Task TriggerEventAsync<T>(string eventName, T value)
    {
        ArgumentValidation.ValidateString(nameof(eventName), eventName);

        var channelName = CacheCommon.EventPrefix + eventName;

        await _subscriber.PublishAsync(RedisChannel.Literal(channelName), 
            new ChannelEvent<T>(_currentClientName, value).Serialize());
        await SaveLastEventAsync(eventName, value);
    }

    /// <inheritdoc />
    public async Task<IOperationInvokeChannel<TInvoke, TResult>> SubscribeInvokeOperationAsync<TInvoke, TResult>(string operationName)
    {
        ArgumentValidation.ValidateString(nameof(operationName), operationName);

        var channelName = CacheCommon.OperationPrefix + operationName;
        var channelMessageQueue = await _subscriber.SubscribeAsync(RedisChannel.Literal(channelName));
        return new OperationInvokeChannel<TInvoke, TResult>(_currentClientName, _subscriber, operationName, channelMessageQueue);
    }
    
    /// <inheritdoc />
    public async Task<TResult> InvokeOperationAsync<TInvoke, TResult>(string operationName, TInvoke arguments)
    {
        ArgumentValidation.ValidateString(nameof(operationName), operationName);

        Guid operationId = Guid.NewGuid();
        var requestChannelName = CacheCommon.OperationPrefix + operationName;
        var responseChannelName = CacheCommon.OperationPrefix + operationName + operationId;

        await _subscriber.PublishAsync(RedisChannel.Literal(requestChannelName), 
            new ChannelOperationInvoke<TInvoke>(_currentClientName, operationId, arguments).Serialize());

        var resultChannel = new OperationResultChannel<TResult>(_currentClientName, await _subscriber.SubscribeAsync(
            RedisChannel.Literal(responseChannelName)));

        return await resultChannel.Task;
    }
    
    /// <inheritdoc />
    public async Task CacheStreamAsync(string cacheStreamKey, byte[] value, string contentType, TimeSpan? expiry = null)
    {
        var prefix = CacheCommon.FilePrefix + cacheStreamKey;
        await _database.StringSetAsync(prefix + "contentType", contentType);
        await _database.KeyExpireAsync(prefix + "contentType", DateTime.Now + expiry);
        await _database.StringSetAsync(prefix + "value", value);
        await _database.KeyExpireAsync(prefix + "value", DateTime.Now + expiry);
    }

    /// <inheritdoc />
    public async Task DeleteCacheStreamAsync(string cacheStreamKey)
    {
        var prefix = CacheCommon.FilePrefix + cacheStreamKey;
        await _database.KeyDeleteAsync(prefix + "contentType");
        await _database.KeyDeleteAsync(prefix + "value");
    }

    /// <inheritdoc />
    public async Task<CacheStream?> GetCacheStreamAsync(string cacheStreamKey)
    {
        var prefix = CacheCommon.FilePrefix + cacheStreamKey;
        var contentType = (string?)await _database.StringGetAsync(prefix + "contentType");
        var fileArray = (byte[]?)await _database.StringGetAsync(prefix + "value");
        if (string.IsNullOrWhiteSpace(contentType) || fileArray == null || fileArray.Length == 0)
        {
            return null;
        }

        return new CacheStream { ContentType = contentType, Stream = fileArray };
    }

    /// <summary>
    ///     Returns true when the channel is connected
    /// </summary>
    public bool IsConnected => _redis.IsConnected;


    private async Task SaveLastEventAsync<T>(string eventName, T value)
    {
        await _database.ListRightPushAsync($"{CacheCommon.LastEventPrefix}{eventName}", value?.Serialize());
    }
}