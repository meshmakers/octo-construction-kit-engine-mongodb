using System;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Implements a distributed cache with pub sub mechanisms using REDIS
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class DistributedWithPubSubCache : IDistributedWithPubSubCache
{
    private readonly ConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private readonly IDatabase _database;

    /// <summary>
    ///     Constructor
    /// </summary>
    public DistributedWithPubSubCache(IOptions<DistributeCacheWithPubSubOptions> options)
    {
        var connectionString = options.Value.Host;
        if (!string.IsNullOrWhiteSpace(options.Value.Password))
        {
            connectionString += $",password={options.Value.Password}";
        }

        _redis = ConnectionMultiplexer.Connect(connectionString);
        _database = _redis.GetDatabase();
        _subscriber = _redis.GetSubscriber();
    }
    
    /// <summary>
    /// Returns the last message of the given channel as string
    /// </summary>
    /// <param name="channelName">The channel name</param>
    /// <returns></returns>
    public async Task<TValue?> GetLastMessageAsync<TValue>(string channelName)
    {
        var lastMessage = await _database.ListGetByIndexAsync($"last_message:{channelName}", -1);
        if (string.IsNullOrWhiteSpace(lastMessage))
        {
            return default;
        }
        return lastMessage.ToString().Deserialize<TValue>();
    }

    /// <summary>
    ///     Subscribes to a channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <typeparam name="TValue">Type of value in messages</typeparam>
    /// <returns>The channel to access e. g. messages</returns>
    public IChannel<TValue> Subscribe<TValue>(string channel) 
    {
        return new Channel<TValue>(_redis.ClientName, _subscriber.Subscribe(RedisChannel.Literal(channel)));
    }

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    public async Task PublishAsync<T>(string channel, T value)
    {
        ArgumentValidation.ValidateString(nameof(channel), channel);
        
        await _subscriber.PublishAsync(RedisChannel.Literal(channel),new ChannelMessage<T>(_redis.ClientName, value).Serialize());
        await SaveLastMessage(channel, value);
    }

    /// <inheritdoc />
    public async Task CacheStreamAsync(string key, byte[] value, string contentType, TimeSpan? expiry = null)
    {
        await _database.StringSetAsync(key + "value", value);
        await _database.StringSetAsync(key + "", contentType);
        await _database.KeyExpireAsync(key + "contentType", DateTime.Now + expiry);
        await _database.KeyExpireAsync(key + "value", DateTime.Now + expiry);
    }

    /// <inheritdoc />
    public async Task DeleteCacheStreamAsync(string key)
    {
        await _database.KeyDeleteAsync(key + "contentType");
        await _database.KeyDeleteAsync(key + "value");
    }

    /// <inheritdoc />
    public async Task<CacheStream?> GetCacheStreamAsync(string key)
    {
        var contentType = (string?)await _database.StringGetAsync(key + "contentType");
        var fileArray = (byte[]?)await _database.StringGetAsync(key + "value");
        if (string.IsNullOrWhiteSpace(contentType) || fileArray == null || fileArray.Length == 0)
        {
            return null;
        }
        
        return new CacheStream{ContentType = contentType, Stream = fileArray};
    }

    /// <summary>
    ///     Returns true when the channel is connected
    /// </summary>
    public bool IsConnected => _redis.IsConnected;

    
    private async Task SaveLastMessage<T>(string channelName, T value)
    {
        await _database.ListRightPushAsync($"last_message:{channelName}", value?.Serialize());
    }
}
