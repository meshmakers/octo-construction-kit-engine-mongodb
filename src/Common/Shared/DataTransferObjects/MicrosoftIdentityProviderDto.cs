using System.ComponentModel.DataAnnotations;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Identity provider configuration specifically for Microsoft accounts.
/// </summary>
public class MicrosoftIdentityProviderDto : IdentityProviderDto
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public MicrosoftIdentityProviderDto()
    {
        Type = IdentityProviderTypesDto.Microsoft;
    }

    /// <summary>
    ///     client id
    /// </summary>
    [Required]
    public string? ClientId { get; set; }

    /// <summary>
    ///     client secret
    /// </summary>
    [Required]
    public string? ClientSecret { get; set; }
}
