using System;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Implements a channel
/// </summary>
/// <typeparam name="TValue">Type of value in messages</typeparam>
internal class Channel<TValue> : IChannel<TValue> 
{
    private readonly string _currentClientName;
    private readonly ChannelMessageQueue _channelMessageQueue;

    internal Channel(string currentClientName, ChannelMessageQueue channelMessageQueue)
    {
        _currentClientName = currentClientName;
        _channelMessageQueue = channelMessageQueue;
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync()
    {
        await _channelMessageQueue.UnsubscribeAsync();
    }

    /// <inheritdoc />
    public void OnMessage(Func<IChannelMessage<TValue>, Task> action)
    {
        _channelMessageQueue.OnMessage(async channelMessage =>
        {
            if (!channelMessage.Message.HasValue)
            {
                return;
            }

            string? serializedObject = channelMessage.Message;
            if (string.IsNullOrWhiteSpace(serializedObject))
            {
                return;
            }

            var o = serializedObject.Deserialize<ChannelMessage<TValue>>();
            if (o.SenderClientName == _currentClientName)
            {
                return;
            }
            
            await action(o);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channelMessageQueue.Unsubscribe();
    }
}
