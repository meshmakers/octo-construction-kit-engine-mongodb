using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Dto for the creation of the initial admin user.
/// </summary>
public class AdminUserDto
{
    /// <summary>
    ///     The users email. This also serves as the username.
    /// </summary>
    [Required]
    [EmailAddress]
    [JsonPropertyName("email")]
    // Email max length is 254, see https://stackoverflow.com/questions/386294/what-is-the-maximum-length-of-a-valid-email-address
    [StringLength(ValidationConstants.EmailMaxLength)]
    public string? EMail { get; set; }

    /// <summary>
    ///     The login password for the user.
    /// </summary>
    [Required]
    [JsonPropertyName("password")]
    [StringLength(ValidationConstants.PasswordMaxLength)]
    public string? Password { get; set; }
}
