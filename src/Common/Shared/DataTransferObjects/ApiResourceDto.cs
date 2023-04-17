using System.Collections.Generic;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class ApiResourceDto
{
    public bool IsEnabled { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool ShowInDiscoveryDocument { get; set; }
    public ICollection<string>? UserClaims { get; set; }
    public bool RequireResourceIndicator { get; set; }
    public ICollection<string>?  Scopes { get; set; }
    public ICollection<string>? AllowedAccessTokenSigningAlgorithms { get; set; }
}
