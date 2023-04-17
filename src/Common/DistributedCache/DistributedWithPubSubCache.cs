using System;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Meshmakers.Octo.Backend.DistributedCache;

/// <summary>
///     Implements a distributed cache with pub sub mechanisms using REDIS
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class DistributedWithPubSubCache : IDistributedWithPubSubCache
{
    private readonly ConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;

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
        Database = _redis.GetDatabase();
        _subscriber = _redis.GetSubscriber();
    }

    /// <summary>
    ///     Subscribes to a channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <typeparam name="TValue">Type of value in messages</typeparam>
    /// <returns>The channel to access e. g. messages</returns>
    public IChannel<TValue> Subscribe<TValue>(string channel) where TValue : IConvertible
    {
        return new Channel<TValue>(_subscriber.Subscribe(channel));
    }

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    public async Task PublishAsync(string channel, string value)
    {
        ArgumentValidation.ValidateString(nameof(channel), channel);
        await _subscriber.PublishAsync(channel, value);
    }

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    public async Task PublishAsync(string channel, int value)
    {
        ArgumentValidation.ValidateString(nameof(channel), channel);
        await _subscriber.PublishAsync(channel, value);
    }

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    public async Task PublishAsync(string channel, long value)
    {
        ArgumentValidation.ValidateString(nameof(channel), channel);
        await _subscriber.PublishAsync(channel, value);
    }

    /// <summary>
    ///     Publish a message to the given channel
    /// </summary>
    /// <param name="channel">The name of the channel</param>
    /// <param name="value">Value to publish</param>
    public void PublishAsync(string channel, double value)
    {
        ArgumentValidation.ValidateString(nameof(channel), channel);
        _subscriber.PublishAsync(channel, value);
    }

    /// <summary>
    ///     Returns true when the channel is connected
    /// </summary>
    public bool IsConnected => _redis.IsConnected;

    /// <summary>
    ///     Returns the Redis database
    /// </summary>
    public IDatabase Database { get; }
}
