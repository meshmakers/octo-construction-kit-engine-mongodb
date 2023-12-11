using Meshmakers.Octo.Common.DistributedCache.Payloads;
using Meshmakers.Octo.Common.Shared;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache.Channels;

/// <summary>
///     Implements an event channel
/// </summary>
/// <typeparam name="TValue">Type of value of event</typeparam>
internal class EventChannel<TValue> : IEventChannel<TValue>
{
    private readonly ChannelMessageQueue _channelMessageQueue;
    private readonly string _currentClientName;

    internal EventChannel(string currentClientName, ChannelMessageQueue channelMessageQueue)
    {
        _currentClientName = currentClientName;
        _channelMessageQueue = channelMessageQueue;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync()
    {
        return _channelMessageQueue.UnsubscribeAsync();
    }

    /// <inheritdoc />
    public void OnEvent(Func<TValue?, Task> action)
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

            var o = serializedObject.Deserialize<ChannelEvent<TValue>>();
            if (o.SenderClientName == _currentClientName)
            {
                return;
            }

            await action(o.Message);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channelMessageQueue.Unsubscribe();
    }
}