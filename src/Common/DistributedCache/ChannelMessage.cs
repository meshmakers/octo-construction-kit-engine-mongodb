namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Implements a channel message
/// </summary>
internal record ChannelMessage<TValue> : IChannelMessage<TValue>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    /// <param name="message">Message of channel</param>
    internal ChannelMessage(string senderClientName, TValue? message)
    {
        SenderClientName = senderClientName;
        Message = message;
    }
    
    /// <summary>
    /// Constructor
    /// </summary>
    internal ChannelMessage()
    {
    }

    /// <inheritdoc />
    public string SenderClientName { get; init; } = null!;

    /// <inheritdoc />
    public TValue? Message { get; init; }
}
