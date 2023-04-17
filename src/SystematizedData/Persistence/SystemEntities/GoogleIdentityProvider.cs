using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

/// <summary>
///     Identity provider configuration specifically for Google accounts.
/// </summary>
public class GoogleIdentityProvider : OctoIdentityProvider
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public GoogleIdentityProvider()
    {
        Type = IdentityProviderTypes.Google;
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
