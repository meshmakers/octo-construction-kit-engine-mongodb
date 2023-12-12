using Meshmakers.Octo.Common.DistributedCache.Payloads;
using Meshmakers.Octo.Common.Shared;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache.Channels;

/// <summary>
/// Implements an operation channel
/// </summary>
/// <typeparam name="TInvoke">Type of operation arguments</typeparam>
/// <typeparam name="TResult">Type of operation results</typeparam>
public class OperationInvokeChannel<TInvoke, TResult> : IOperationInvokeChannel<TInvoke, TResult>
{
    private readonly ChannelMessageQueue _channelMessageQueue;
    private readonly string _currentClientName;
    private readonly string _channelName;
    private readonly ISubscriber _subscriber;

    internal OperationInvokeChannel(string currentClientName, ISubscriber subscriber, string operationName, 
        ChannelMessageQueue channelMessageQueue)
    {
        _channelName = CacheCommon.OperationPrefix + operationName;
        _currentClientName = currentClientName;
        _subscriber = subscriber;
        _channelMessageQueue = channelMessageQueue;
    }

    /// <inheritdoc />
    public void OnInvoked(Func<TInvoke, Task<TResult>> action)
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

            var o = serializedObject.Deserialize<ChannelOperationInvoke<TInvoke>>();
            if (o.SenderClientName == _currentClientName)
            {
                return;
            }

            TResult? result = default;
            OperationError? error = default;
            try
            {
                result = await action(o.Arguments);
            }
            catch (DistributedOperationFailedException<OperationError> e)
            {
                error = e.Error;
            }
       
            var resultMessage = new ChannelOperationResult<TResult>(_currentClientName, o.OperationId, result, error);
            await _subscriber.PublishAsync(RedisChannel.Literal(_channelName), resultMessage.Serialize());
        });
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync()
    {
        return _channelMessageQueue.UnsubscribeAsync();
    }
    
    /// <inheritdoc />
    public void Dispose()
    {
        _channelMessageQueue.Unsubscribe();
    }
}