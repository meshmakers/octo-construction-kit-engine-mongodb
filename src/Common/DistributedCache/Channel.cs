using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Meshmakers.Octo.Backend.DistributedCache;

/// <summary>
///     Implements a channel
/// </summary>
/// <typeparam name="TValue">Type of value in messages</typeparam>
public class Channel<TValue> : IChannel<TValue> where TValue : IConvertible
{
    private readonly ChannelMessageQueue _channelMessageQueue;

    internal Channel(ChannelMessageQueue channelMessageQueue)
    {
        _channelMessageQueue = channelMessageQueue;
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync()
    {
        await _channelMessageQueue.UnsubscribeAsync();
    }

    /// <inheritdoc />
    public void OnMessage(Func<ChannelMessage<TValue>, Task> action)
    {
        _channelMessageQueue.OnMessage(async message =>
        {
            if (!message.Message.HasValue)
            {
                await action(new ChannelMessage<TValue>(default, false));
            }

            if (typeof(TValue) == typeof(int))
            {
                if (message.Message.TryParse(out int _))
                {
                    await action(new ChannelMessage<TValue>((TValue)Convert.ChangeType(message.Message, TypeCode.Int32),
                        true));
                }
            }
            else if (typeof(TValue) == typeof(double))
            {
                if (message.Message.TryParse(out double _))
                {
                    await action(
                        new ChannelMessage<TValue>((TValue)Convert.ChangeType(message.Message, TypeCode.Double), true));
                }
            }
            else if (typeof(TValue) == typeof(long))
            {
                if (message.Message.TryParse(out long _))
                {
                    await action(new ChannelMessage<TValue>((TValue)Convert.ChangeType(message.Message, TypeCode.Int64),
                        true));
                }
            }
            else if (typeof(TValue) == typeof(string))
            {
                await action(new ChannelMessage<TValue>((TValue)Convert.ChangeType(message.Message, TypeCode.String),
                    true));
            }

            await action(new ChannelMessage<TValue>(default, true));
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channelMessageQueue.Unsubscribe();
    }
}
