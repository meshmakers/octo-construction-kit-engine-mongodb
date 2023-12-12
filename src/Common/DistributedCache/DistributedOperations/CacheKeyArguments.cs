namespace Meshmakers.Octo.Common.DistributedCache.DistributedOperations;

/// <summary>
/// Arguments for import runtime data
/// </summary>
public record CacheKeyArguments: ArgumentBase
{
    /// <summary>
    /// Returns the cache file key
    /// </summary>
    public string CacheFileKey { get; }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="cacheFileKey"></param>
    public CacheKeyArguments(string tenantId, string cacheFileKey) 
        : base(tenantId)
    {
        CacheFileKey = cacheFileKey;
    }
}