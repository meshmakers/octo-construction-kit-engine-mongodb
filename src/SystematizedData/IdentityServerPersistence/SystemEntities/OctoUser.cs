using Meshmakers.Octo.Common.Shared;
using Microsoft.AspNetCore.Identity;

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

/// <summary>
///     Represents an user identity.
/// </summary>
/// <remarks>
///     Add profile data for application users by adding properties to the IdentityUser class
/// </remarks>
public sealed class OctoUser : IdentityUser<OctoObjectId>
{
    public OctoUser()
    {
        Roles = new List<string>();
        Claims = new List<IdentityUserClaim<string>>();
        Logins = new List<IdentityUserLogin<string>>();
        Tokens = new List<IdentityUserToken<string>>();
    }

    public OctoUser(string userName)
        : this()
    {
        UserName = userName;
        NormalizedUserName = userName.ToUpperInvariant();
    }

    public List<string> Roles { get; set; }

    public List<IdentityUserClaim<string>> Claims { get; set; }

    public List<IdentityUserLogin<string>> Logins { get; set; }

    public List<IdentityUserToken<string>> Tokens { get; set; }

    public override string? UserName { get; set; }

    /// <summary>
    ///     The first name of the user.
    /// </summary>
    public string? FirstName { get; set; }

    /// <summary>
    ///     The last name of the user.
    /// </summary>
    public string? LastName { get; set; }

    /// <summary>
    ///     Force the user to change the password after the next login.
    /// </summary>
    public bool ResetPasswordOnLogin { get; set; }

    /// <summary>
    ///     Further information on the user.
    /// </summary>
    public string? Description { get; set; }
}
