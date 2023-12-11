namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
/// Interface of a channel message
/// </summary>
public interface IChannelMessage
{
    /// <summary>
    ///     Return the client name of the sender.
    /// </summary>
    /// <remarks>
    ///     Must be unique in the in redis instance.
    /// </remarks>
    string SenderClientName { get; }
}