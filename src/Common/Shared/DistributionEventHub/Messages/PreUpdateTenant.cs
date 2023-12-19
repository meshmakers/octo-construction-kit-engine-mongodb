namespace Meshmakers.Octo.Common.Shared.DistributionEventHub.Messages;

/// <summary>
/// Message in distribution event hub before a tenant gets modified
/// </summary>
public record PreUpdateTenant(string TenantId)
{
}