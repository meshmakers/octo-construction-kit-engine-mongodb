namespace Meshmakers.Octo.Common.Shared.DistributedCache;

/// <summary>
///     Interface of a channel message
/// </summary>
public interface IChannelMessage<out TValue>
{
    /// <summary>
    ///     Returns the message
    /// </summary>
    TValue? Message { get; }
    
    /// <summary>
    /// Return the client name of the sender.
    /// </summary>
    /// <remarks>
    /// Must be unique in the in redis instance.
    /// </remarks>
    string SenderClientName {get;}
}
