using Meshmakers.Octo.Common.DistributedCache.Payloads;
using Meshmakers.Octo.Common.Shared;
using StackExchange.Redis;

namespace Meshmakers.Octo.Common.DistributedCache.Channels;

/// <summary>
/// Implements an operation result channel
/// </summary>
/// <typeparam name="TResult">Result type of operation results</typeparam>
/// <typeparam name="TError">Error type of operation results</typeparam>
public class OperationResultChannel<TError, TResult> : IOperationResultChannel<TError, TResult>
{
    private readonly string _currentClientName;
    private readonly ChannelMessageQueue _channelMessageQueue;

    internal OperationResultChannel(string currentClientName, 
        ChannelMessageQueue channelMessageQueue)
    {
        _currentClientName = currentClientName;
        _channelMessageQueue = channelMessageQueue;
    }

    /// <inheritdoc />
    public void OnSuccess(Func<TResult, Task> action)
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

            var o = serializedObject.Deserialize<ChannelOperationResult<TResult>>();
            if (o.SenderClientName == _currentClientName)
            {
                return;
            }

            await action(o.Result);
        });
    }

    /// <inheritdoc />
    public void OnError(Func<TError, Task> action)
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

            var o = serializedObject.Deserialize<ChannelOperationError<TError>>();
            if (o.SenderClientName == _currentClientName)
            {
                return;
            }

            await action(o.Error);
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