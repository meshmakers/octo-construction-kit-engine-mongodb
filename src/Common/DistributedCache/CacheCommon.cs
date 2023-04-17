namespace Meshmakers.Octo.Backend.DistributedCache;

/// <summary>
///     Common definitions of cache
/// </summary>
public static class CacheCommon
{
    /// <summary>
    ///     Channel of identity provider updates
    /// </summary>
    public const string KeyIdentityProviderUpdate = "IdentityProviderUpdate";

    /// <summary>
    ///     Key where the cors policy definitions are stored
    /// </summary>
    public const string KeyCorsClients = "CorsPolicyProvider_CorsClients";

    /// <summary>
    ///     Channel of tenant updates
    /// </summary>
    public const string KeyTenantUpdate = "TenantUpdate";
}
