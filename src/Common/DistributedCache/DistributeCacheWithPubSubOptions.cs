namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Options of distributed cache with pub sub
/// </summary>
public class DistributeCacheWithPubSubOptions
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public DistributeCacheWithPubSubOptions()
    {
        Host = "localhost";
    }

    /// <summary>
    ///     The name of the redis hosts, can be a list separated by','
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    ///     The password to connect to redis
    /// </summary>
    public string? Password { get; set; }
}