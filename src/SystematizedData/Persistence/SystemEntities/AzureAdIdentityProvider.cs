using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

/// <summary>
///     Identity provider configuration specifically for Azure Active Directory.
/// </summary>
public class AzureAdIdentityProvider : OctoIdentityProvider
{
#pragma warning disable 1591
    public const string DefaultAuthority = "https://login.microsoftonline.com";
#pragma warning restore 1591
    /// <summary>
    ///     Constructor
    /// </summary>
    public AzureAdIdentityProvider()
    {
        Type = IdentityProviderTypes.MicrosoftActiveDirectory;
    }

    /// <summary>
    ///     The Tenant ID for the Azure Active Directory.
    /// </summary>
    [Required]
    public string TenantId { get; set; }

    /// <summary>
    ///     Authority (default value: https://login.microsoftonline.com).
    /// </summary>
    [Required]
    public string Authority { get; set; } = DefaultAuthority;

    /// <summary>
    ///     Client ID (group Azure AD).
    /// </summary>
    [Required]
    public string ClientIdGroupAzureAd { get; set; }

    /// <summary>
    ///     Client ApiSecret (group Azure AD).
    /// </summary>
    [Required]
    public string ClientSecretGroupAzureAd { get; set; }

    /// <summary>
    ///     Client ID (group Graph API).
    /// </summary>
    [Required]
    public string ClientIdGroupGraphApi { get; set; }
}
