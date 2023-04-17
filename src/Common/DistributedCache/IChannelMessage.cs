using System;

namespace Meshmakers.Octo.Backend.DistributedCache;

/// <summary>
///     Interface of a channel message
/// </summary>
public interface IChannelMessage<out TValue> where TValue : IConvertible
{
    /// <summary>
    ///     Returns the message
    /// </summary>
    TValue? Message { get; }

    /// <summary>
    ///     Returns true if there is a value
    /// </summary>
    bool HasValue { get; }
}
