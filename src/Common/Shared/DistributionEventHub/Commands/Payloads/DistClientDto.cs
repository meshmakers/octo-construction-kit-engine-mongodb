namespace Meshmakers.Octo.Common.Shared.DistributionEventHub.Commands.Payloads;

/// <summary>
/// Represents a client.
/// </summary>
/// <param name="ClientId">Client id.</param>
/// <param name="ClientName">Client name.</param>
/// <param name="ClientUri">Client uri.</param>
public record DistClientDto(string ClientId, string ClientName, string ClientUri)
{
    /// <summary>
    /// Gets or sets allowed grant types.
    /// </summary>
    public string[] AllowedGrantTypes { get; init; } = null!;
    
    /// <summary>
    /// Gets or sets if a consent is required.
    /// </summary>
    public bool RequireConsent { get; init; }

    /// <summary>
    /// Gets or sets redirect uris.
    /// </summary>
    public ICollection<string> RedirectUris { get; } = new List<string>();
    
    /// <summary>
    /// Gets or sets post logout redirect uris.
    /// </summary>
    public ICollection<string> PostLogoutRedirectUris { get; } = new List<string>();
    
    /// <summary>
    /// Gets or sets allowed cors origins.
    /// </summary>
    public ICollection<string> AllowedCorsOrigins { get; } = new List<string>();
    
    /// <summary>
    /// Gets or sets allowed scopes.
    /// </summary>
    public ICollection<string> AllowedScopes { get; } = new List<string>();

    /// <summary>
    /// Gets or sets if offline access is allowed.
    /// </summary>
    public bool AllowOfflineAccess { get; init; }
}