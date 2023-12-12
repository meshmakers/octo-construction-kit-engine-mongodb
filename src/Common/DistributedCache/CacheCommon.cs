namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Common definitions of cache
/// </summary>
public static class CacheCommon
{
    internal const string FilePrefix = "CacheFile_";
    internal const string EventPrefix = "CacheEvent_";
    internal const string OperationPrefix = "CacheOperation_";
    internal const string LastEventPrefix = "LastCacheEvent_";
    
    
    /// <summary>
    ///     Channel of identity provider updates
    /// </summary>
    public const string KeyIdentityProviderUpdate = "IdentityProviderUpdate";

    /// <summary>
    ///     Key where the cors policy definitions are stored
    /// </summary>
    public const string KeyCorsClients = "CorsPolicyProvider_CorsClients";

    /// <summary>
    ///     Key of tenant updates for pre processing
    /// </summary>
    public const string KeyTenantPreUpdate = "TenantUpdatePre";

    /// <summary>
    ///     Key of tenant updates for post processing
    /// </summary>
    public const string KeyTenantPostUpdate = "TenantUpdatePost";

    /// <summary>
    ///     Channel of communication controller pool updates
    /// </summary>
    public const string KeyCommunicationControllerPoolUpdate = "CommunicationController_Pools_Update";

    /// <summary>
    ///     Channel of communication controller plug updates
    /// </summary>
    public const string KeyCommunicationControllerPlugUpdate = "CommunicationController_Plugs_Update";

    /// <summary>
    ///     Channel of communication controller socket updates
    /// </summary>
    public const string KeyCommunicationControllerSocketUpdate = "CommunicationController_Socket_Update";
}