using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

namespace Persistence.IdentityCkModel.CkModelEntities;

/// <summary>
/// Represents a claim that a user possesses.
/// </summary>
public class RtIdentityUserClaim
{
    /// <summary>
    /// Gets or sets the identifier for this user claim.
    /// </summary>
    public virtual int Id { get; set; } = default!;

    /// <summary>
    /// Gets or sets the primary key of the user associated with this claim.
    /// </summary>
    public virtual OctoObjectId UserId { get; set; } = default!;

    /// <summary>
    /// Gets or sets the claim type for this claim.
    /// </summary>
    public virtual string? ClaimType { get; set; }

    /// <summary>
    /// Gets or sets the claim value for this claim.
    /// </summary>
    public virtual string? ClaimValue { get; set; }
}