using System.Security.Claims;

namespace Meshmakers.Octo.Common.Shared.Authorization;

/// <summary>
///     Represents the response of a user info endpoint request
/// </summary>
public class UserInfoData
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="isAuthenticated"></param>
    /// <param name="claims"></param>
    public UserInfoData(bool isAuthenticated, IEnumerable<Claim>? claims)
    {
        IsAuthenticated = isAuthenticated;
        Claims = claims;
    }

    /// <summary>
    ///     Returns claims from user info endpoint
    /// </summary>
    public IEnumerable<Claim>? Claims { get; }

    /// <summary>
    ///     Returns true if the aut
    /// </summary>
    public bool IsAuthenticated { get; }
}