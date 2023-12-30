using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Identity provider configuration specifically for OpenLDAP.
/// </summary>
public class OpenLdapProviderDto : IdentityProviderDto
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    public OpenLdapProviderDto()
    {
        Type = IdentityProviderTypesDto.OpenLdap;
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
    ///     User base DN
    /// </summary>
    [Required]
    public string UserBaseDn { get; set; } = null!;

    /// <summary>
    ///     User name attribute
    /// </summary>
    [Required]
    public string UserNameAttribute { get; set; } = DefaultUserNameAttribute;

    /// <summary>
    ///     User distinguished name.
    /// </summary>
    [Required]
    public string UserDistinguishedName { get; set; } = null!;

    /// <summary>
    ///     Password.
    /// </summary>
    [Required]
    public string Password { get; set; } = null!;

    /// <summary>
    ///     Whether to use TLS for connecting to the directory server.
    /// </summary>
    public bool ApplyTlsEncryption { get; set; } = true;

#pragma warning disable 1591
    public const int DefaultPort = 636;
    public const string DefaultUserNameAttribute = "uid";
#pragma warning restore 1591
}