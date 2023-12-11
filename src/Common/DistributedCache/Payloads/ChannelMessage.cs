namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
/// Implementation of basics of a channel message
/// </summary>
public abstract record ChannelMessage : IChannelMessage
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    protected ChannelMessage(string senderClientName)
    {
        SenderClientName = senderClientName;
    }
    
    /// <inheritdoc />
    public string SenderClientName { get; }
}