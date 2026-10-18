using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// A recompute, a forward aggregation or a sweep was refused because the tenant is marked <em>restore reconcile pending</em>
/// (AB#5967 slice 2 / AB#6968): a restore with archive data finished and its rollups were neither validated ok nor repaired yet.
/// The orchestrator ticks of the host do not even try (they skip the tenant); this is what a direct call gets.
/// </summary>
public sealed class RestoreReconcilePendingException : Contracts.StreamData.StreamDataException
{
    public RestoreReconcilePendingException(string tenantId, OctoObjectId archiveRtId, string refused)
        : base($"Tenant '{tenantId}' is marked restore reconcile pending: the {refused} of rollup '{archiveRtId}' is not run until the " +
               "rollups validate ok or a repair has run (IStreamDataRepository.RepairRollupsAsync clears the marker when the tenant is consistent).",
            archiveRtId)
    {
    }
}
