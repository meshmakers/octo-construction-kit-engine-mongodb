using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

/// <summary>
///     Identity provider configuration specifically for Microsoft accounts.
/// </summary>
public class MicrosoftIdentityProvider : OctoIdentityProvider
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public MicrosoftIdentityProvider()
    {
        Type = IdentityProviderTypes.Microsoft;
    }

    /// <summary>
    ///     client id
    /// </summary>
    [Required]
    public string ClientId { get; set; }

    /// <summary>
    ///     client secret
    /// </summary>
    [Required]
    public string ClientSecret { get; set; }
}
