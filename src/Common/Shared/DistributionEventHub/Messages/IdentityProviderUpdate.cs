namespace Meshmakers.Octo.Common.Shared.DistributionEventHub.Messages;

/// <summary>
/// Used to signal that identity provider configuration for a tenant is updated.
/// </summary>
/// <param name="TenantId"></param>
public record IdentityProviderUpdate(string? TenantId);