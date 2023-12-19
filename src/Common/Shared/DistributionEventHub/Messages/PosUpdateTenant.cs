namespace Meshmakers.Octo.Common.Shared.DistributionEventHub.Messages;

/// <summary>
/// Message in distribution event hub after a tenant gets modified
/// </summary>
public record PosUpdateTenant(string TenantId)
{
}