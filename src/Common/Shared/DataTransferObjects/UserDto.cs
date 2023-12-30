using System.ComponentModel.DataAnnotations;

// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable PropertyCanBeMadeInitOnly.Global

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class UserDto
{
    /// <summary>
    ///     Gets or sets the first name
    /// </summary>
    public string? FirstName { get; set; }

    /// <summary>
    ///     Gets or sets the last name
    /// </summary>
    public string? LastName { get; set; }

    /// <summary>
    ///     Gets or sets the user id
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    ///     Gets or sets the E-Mail address of the user
    /// </summary>
    [Required]
    public string? Email { get; set; }

    /// <summary>
    ///     Gets or sets the display name
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets roles of the user
    /// </summary>
    public IEnumerable<RoleDto>? Roles { get; set; }

    /// <summary>
    ///     User is requested to reset password on log-in
    /// </summary>
    public bool ResetPasswordOnLogin { get; set; }
}

public class RegisterUserDto : UserDto
{
    /// <summary>
    ///     The user password. This is only transferred when creating a new user.
    /// </summary>
    public string? Password { get; set; }
}