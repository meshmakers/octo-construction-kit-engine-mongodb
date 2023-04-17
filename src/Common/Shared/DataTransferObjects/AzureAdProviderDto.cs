using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
/// Identity provider configuration specifically for Azure Active Directory.
/// </summary>
public class AzureAdProviderDto : IdentityProviderDto
{
    /// <summary>
    /// Constructor
    /// </summary>
    public AzureAdProviderDto()
    {
        Type = IdentityProviderTypesDto.MicrosoftAzureAd;
    }

#pragma warning disable 1591
    public const string DefaultApiGroupGraphApi = "https://graph.microsoft.com";
    public const string DefaultAuthority = "https://login.microsoftonline.com";
#pragma warning restore 1591

    /// <summary>
    /// The Tenant ID for the Azure Active Directory.
    /// </summary>
    [Required]
    public string TenantId { get; set; } = null!;

    /// <summary>
    /// Authority (default value: https://login.microsoftonline.com).
    /// </summary>
    [Required]
    public string Authority { get; set; } = DefaultAuthority;

    /// <summary>
    /// Client ID (group Azure AD).
    /// </summary>
    [Required]
    public string ClientIdGroupAzureAd { get; set; } = null!;

    /// <summary>
    /// Client Secret (group Azure AD).
    /// </summary>
    [Required]
    public string ClientSecretGroupAzureAd { get; set; } = null!;

    /// <summary>
    /// Client ID (group Graph API).
    /// </summary>
    [Required]
    public string ClientIdGroupGraphApi { get; set; } = null!;

    /// <summary>
    /// Client Secret (group Graph API).
    /// </summary>
    [Required]
    public string ClientSecretGroupGraphApi { get; set; } = null!;

    /// <summary>
    /// API (group Graph API) (default value: https://graph.microsoft.com).
    /// </summary>
    [Required]
    public string ApiGroupGraphApi { get; set; } = DefaultApiGroupGraphApi;
}
