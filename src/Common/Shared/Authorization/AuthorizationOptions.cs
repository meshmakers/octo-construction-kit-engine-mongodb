namespace Meshmakers.Octo.Common.Shared.Authorization;

public class AuthorizationOptions
{
    public string IssuerUri { get; set; } = null!;
    public string ClientId { get; set; } = null!;
    public string? ClientSecret { get; set; }
}
