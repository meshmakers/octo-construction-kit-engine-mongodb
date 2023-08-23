using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Persistence.IdentityCkModel.CkModelEntities;


/// <summary>
/// Represents an authentication token for a user.
/// </summary>
public class RtIdentityUserToken
{
    /// <summary>
    /// Gets or sets the primary key of the user that the token belongs to.
    /// </summary>
    public OctoObjectId UserId { get; set; } = default!;

    /// <summary>
    /// Gets or sets the LoginProvider this token is from.
    /// </summary>
    public string LoginProvider { get; set; } = default!;

    /// <summary>
    /// Gets or sets the name of the token.
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Gets or sets the token value.
    /// </summary>
    public string? Value { get; set; }
}