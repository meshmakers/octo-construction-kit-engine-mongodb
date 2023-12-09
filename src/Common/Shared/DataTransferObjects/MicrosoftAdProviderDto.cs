using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Identity provider configuration specifically for Microsoft Active Directory.
/// </summary>
public class MicrosoftAdProviderDto : IdentityProviderDto
{
#pragma warning disable 1591
    public const int DefaultPort = 636;
#pragma warning restore 1591
    /// <summary>
    ///     Constructor.
    /// </summary>
    public MicrosoftAdProviderDto()
    {
        Type = IdentityProviderTypesDto.MicrosoftActiveDirectory;
    }

    /// <summary>
    ///     Host.
    /// </summary>
    [Required]
    public string Host { get; set; } = null!;

    /// <summary>
    ///     Port (default port 636).
    /// </summary>
    [Required]
    public ushort Port { get; set; } = DefaultPort;

    /// <summary>
    ///     User principal name.
    /// </summary>
    [Required]
    public string UserPrincipalName { get; set; } = null!;

    /// <summary>
    ///     Password.
    /// </summary>
    [Required]
    public string Password { get; set; } = null!;

    /// <summary>
    ///     Whether to use TLS for connecting to the directory server.
    /// </summary>
    public bool ApplyTlsEncryption { get; set; }
}