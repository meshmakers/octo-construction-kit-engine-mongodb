using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Data Transfer Object of a Octo client
/// </summary>
public class ClientDto
{
    /// <summary>
    ///     Specifies if client is enabled
    /// </summary>
    public bool? IsEnabled { get; set; }

    /// <summary>
    ///     Unique ID of the client
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    ///     Optional client secret
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    ///     Client display name (used for logging and consent screen)
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    ///     URI to further information about client (used on consent screen)
    /// </summary>
    public string? ClientUri { get; set; }

    /// <summary>
    ///     Specifies the allowed grant types (legal combinations of AuthorizationCode, Implicit, Hybrid, ResourceOwner,
    ///     ClientCredentials).
    /// </summary>
    public IEnumerable<string>? AllowedGrantTypes { get; set; }

    /// <summary>
    ///     Specifies allowed URIs to return tokens or authorization codes to
    /// </summary>
    public IEnumerable<string>? RedirectUris { get; set; }

    /// <summary>
    ///     Specifies allowed URIs to redirect to after logout
    /// </summary>
    public IEnumerable<string>? PostLogoutRedirectUris { get; set; }

    /// <summary>
    ///     Gets or sets the allowed CORS origins for JavaScript clients.
    /// </summary>
    public IEnumerable<string>? AllowedCorsOrigins { get; set; }

    /// <summary>
    ///     Specifies the api scopes that the client is allowed to request
    /// </summary>
    public IEnumerable<string>? AllowedScopes { get; set; }

    /// <summary>
    ///     Specifies if offline access to use code_authorization is enabled
    /// </summary>
    public bool? IsOfflineAccessEnabled { get; set; }
}
