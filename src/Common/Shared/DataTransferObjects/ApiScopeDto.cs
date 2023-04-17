using System.Collections.Generic;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class ApiScopeDto
{
    public bool IsEnabled { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool ShowInDiscoveryDocument { get; set; }
    public ICollection<string>? UserClaims { get; set; }
    public bool IsRequired { get; set; }
    public bool IsEmphasize { get; set; }
    public bool RequireResourceIndicator { get; set; }
    public bool ApiSecrets { get; set; }
}
