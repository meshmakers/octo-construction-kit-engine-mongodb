namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

/// <summary>
///     Available types of identity providers.
/// </summary>
public enum IdentityProviderTypes
{
    /// <summary>
    ///     Google Account
    /// </summary>
    Google = 0,
    
    /// <summary>
    ///     Microsoft Account
    /// </summary>
    Microsoft = 1,
    
    /// <summary>
    ///     Azure Active Directory.
    /// </summary>
    MicrosoftAzureAd = 2,

    /// <summary>
    ///     Classic Microsoft Active Directory
    /// </summary>
    MicrosoftActiveDirectory = 3,

    /// <summary>
    ///     Open LDAP
    /// </summary>
    OpenLdap = 4
}
