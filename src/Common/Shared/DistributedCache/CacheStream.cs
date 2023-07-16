using System;

namespace Meshmakers.Octo.Common.Shared.DistributedCache;

/// <summary>
/// Represents a cache stream
/// </summary>
public record CacheStream
{
    /// <summary>
    /// The content type of the stream
    /// </summary>
    public string ContentType { get; set; } = string.Empty;
    
    /// <summary>
    /// The stream as byte array
    /// </summary>
    public byte[] Stream { get; set; } = Array.Empty<byte>();
}