using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

/// <summary>
/// Identity provider configuration specifically for Microsoft Active Directory.
/// </summary>
public class MicrosoftAdIdentityProvider : OctoIdentityProvider
{
    /// <summary>
    /// Constructor.
    /// </summary>
    public MicrosoftAdIdentityProvider()
    {
        Type = IdentityProviderTypes.MicrosoftActiveDirectory;
    }

#pragma warning disable 1591
    public const int DefaultPort = 636;
#pragma warning restore 1591

    /// <summary>
    /// Host.
    /// </summary>
    [Required]
    public string Host { get; set; }

    /// <summary>
    /// Port (default port 636).
    /// </summary>
    [Required]
    public ushort Port { get; set; } = DefaultPort;

    /// <summary>
    /// User principal name.
    /// </summary>
    [Required]
    public string UserPrincipalName { get; set; }

    /// <summary>
    /// Password.
    /// </summary>
    [Required]
    public string Password { get; set; }

    /// <summary>
    /// Whether to use TLS for connecting to the directory server.
    /// </summary>
    public bool ApplyTlsEncryption { get; set; }
}
