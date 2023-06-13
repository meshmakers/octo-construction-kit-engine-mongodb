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
    
    /// <summary>
    /// Channel of plug controller pool updates
    /// </summary>
    public const string KeyPlugControllerPoolUpdate = "PlugController_Pools_Update";
    
    /// <summary>
    /// Channel of plug controller plug updates
    /// </summary>
    public const string KeyPlugControllerPlugUpdate = "PlugController_Plugs_Update";
}
