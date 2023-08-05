using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Persistence.IdentityCkModel.CkModelEntities;

/// <summary>
///     Represents an user identity.
/// </summary>
[CkId(IdentityCkModel.SystemIdentityUser)]
public class RtIdentityUser : RtEntity
{
    /// <summary>
    /// Gets or sets the first name for this user.
    /// </summary>
    public string? FirstName
    {
        get => GetAttributeStringValueOrDefault(nameof(FirstName));
        set => SetAttributeValue(nameof(FirstName), AttributeValueTypes.String, value);
    }
    
    /// <summary>
    /// Gets or sets the last name for this user.
    /// </summary>
    public string? LastName
    {
        get => GetAttributeStringValueOrDefault(nameof(LastName));
        set => SetAttributeValue(nameof(LastName), AttributeValueTypes.String, value);
    }
    
    /// <summary>
    /// Gets or sets the user name for this user.
    /// </summary>
    public string? UserName
    {
        get => GetAttributeStringValueOrDefault(nameof(UserName));
        set => SetAttributeValue(nameof(UserName), AttributeValueTypes.String, value);
    }

    /// <summary>
    /// Gets or sets the normalized user name for this user.
    /// </summary>
    public string? NormalizedUserName
    {
        get => GetAttributeStringValueOrDefault(nameof(NormalizedUserName));
        set => SetAttributeValue(nameof(NormalizedUserName), AttributeValueTypes.String, value);
    }

    /// <summary>
    /// Gets or sets the email address for this user.
    /// </summary>
    public string? Email
    {
        get => GetAttributeStringValueOrDefault(nameof(Email));
        set => SetAttributeValue(nameof(Email), AttributeValueTypes.String, value);
    }
    
    /// <summary>
    /// Gets or sets the normalized email address for this user.
    /// </summary>
    public string? NormalizedEmail
    {
        get => GetAttributeStringValueOrDefault(nameof(NormalizedEmail));
        set => SetAttributeValue(nameof(NormalizedEmail), AttributeValueTypes.String, value);
    }

    /// <summary>
    /// Gets or sets a flag indicating if a user has confirmed their email address.
    /// </summary>
    /// <value>True if the email address has been confirmed, otherwise false.</value>
    public bool EmailConfirmed
    {
        get => GetAttributeValueOrStandard(nameof(EmailConfirmed), false);
        set => SetAttributeValue(nameof(EmailConfirmed), AttributeValueTypes.Boolean, value);
    }
    
    /// <summary>
    /// Gets or sets a salted and hashed representation of the password for this user.
    /// </summary>
    public string? PasswordHash
    {
        get => GetAttributeStringValueOrDefault(nameof(PasswordHash));
        set => SetAttributeValue(nameof(PasswordHash), AttributeValueTypes.String, value);
    }

    /// <summary>
    /// A random value that must change whenever a users credentials change (password changed, login removed)
    /// </summary>
    public string? SecurityStamp
    {
        get => GetAttributeStringValueOrDefault(nameof(SecurityStamp));
        set => SetAttributeValue(nameof(SecurityStamp), AttributeValueTypes.String, value);
    }

    /// <summary>
    /// A random value that must change whenever a user is persisted to the store
    /// </summary>
    public string? ConcurrencyStamp
    {
        get => GetAttributeStringValueOrDefault(nameof(ConcurrencyStamp));
        set => SetAttributeValue(nameof(ConcurrencyStamp), AttributeValueTypes.String, value);
    }
    
    /// <summary>
    /// Gets or sets a telephone number for the user.
    /// </summary>
    public string? PhoneNumber
    {
        get => GetAttributeStringValueOrDefault(nameof(PhoneNumber));
        set => SetAttributeValue(nameof(PhoneNumber), AttributeValueTypes.String, value);
    }
    
    /// <summary>
    /// Gets or sets a flag indicating if a user has confirmed their telephone address.
    /// </summary>
    /// <value>True if the telephone number has been confirmed, otherwise false.</value>
    public bool PhoneNumberConfirmed
    {
        get => GetAttributeValueOrStandard(nameof(PhoneNumberConfirmed), false);
        set => SetAttributeValue(nameof(PhoneNumberConfirmed), AttributeValueTypes.Boolean, value);
    }

    /// <summary>
    /// Gets or sets a flag indicating if two factor authentication is enabled for this user.
    /// </summary>
    /// <value>True if 2fa is enabled, otherwise false.</value>
    public bool TwoFactorEnabled
    {
        get => GetAttributeValueOrStandard(nameof(TwoFactorEnabled), false);
        set => SetAttributeValue(nameof(TwoFactorEnabled), AttributeValueTypes.Boolean, value);
    }
    
    /// <summary>
    /// Gets or sets the date and time, in UTC, when any user lockout ends.
    /// </summary>
    /// <remarks>
    /// A value in the past means the user is not locked out.
    /// </remarks>
    public DateTimeOffset? LockoutEnd
    {
        get => GetAttributeValueOrDefault<DateTimeOffset>(nameof(LockoutEnd));
        set => SetAttributeValue(nameof(LockoutEnd), AttributeValueTypes.DateTime, value);
    }

    /// <summary>
    /// Gets or sets a flag indicating if the user could be locked out.
    /// </summary>
    /// <value>True if the user could be locked out, otherwise false.</value>
    public bool LockoutEnabled
    {
        get => GetAttributeValueOrStandard(nameof(LockoutEnabled), false);
        set => SetAttributeValue(nameof(LockoutEnabled), AttributeValueTypes.Boolean, value);
    }

    /// <summary>
    /// Gets or sets the number of failed login attempts for the current user.
    /// </summary>
    public int? AccessFailedCount
    {
        get => GetAttributeValueOrDefault<int>(nameof(AccessFailedCount));
        set => SetAttributeValue(nameof(AccessFailedCount), AttributeValueTypes.Int, value);
    }
    
    /// <summary>
    /// Gets or sets a flag indicating if the user must change their password on next login.
    /// </summary>
    public bool ResetPasswordOnLogin
    {
        get => GetAttributeValueOrStandard(nameof(ResetPasswordOnLogin), false);
        set => SetAttributeValue(nameof(ResetPasswordOnLogin), AttributeValueTypes.Boolean, value);
    }
    
    /// <summary>
    /// Gets or sets the navigation property for the roles this user belongs to.
    /// </summary>
    // TODO: Continue here
    // public List<string> Roles
    // {
    //     get => GetAttributeValueOrDefault(nameof(Roles), new List<string>());
    //     set => SetAttributeValue(nameof(Roles), AttributeValueTypes.Boolean, value);
    // }

    /// <summary>
    /// Gets or sets the navigation property for the claims this user possesses.
    /// </summary>
    public List<RtIdentityUserClaim> Claims { get; set; } = new();

    /// <summary>
    /// Gets or sets the navigation property for this users login accounts.
    /// </summary>
    public List<RtIdentityUserLogin> Logins { get; set; } = new();

    /// <summary>
    /// Gets or sets the navigation property for this users tokens.
    /// </summary>
    public List<RtIdentityUserToken> Tokens { get; set; } = new();
}
