using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// Answers whether the storage can run the native copy: the cluster setting that keeps COPY statements out of
/// <c>sys.jobs_log</c> and the version of the nodes. Both the tenant-bound
/// <see cref="IStreamDataRepository.CheckStagingCopyReadinessAsync"/> and the cluster-level
/// <see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.IStreamDataRepositoryFactory.CheckStagingCopyReadinessAsync"/>
/// delegate here, so the two can never give different answers.
/// </summary>
/// <remarks>
/// Side-effect free: two reads of cluster state, nothing is written or changed. It never throws for a missing
/// prerequisite (that is the answer it gives); the reason carries no credential and no storage location.
/// The probe touches no tenant data and needs no CK cache - the tenant id only labels the connection.
/// </remarks>
internal static class StagingCopyReadinessProbe
{
    /// <summary>The label of the connection when no tenant is involved (the same one the health check uses).</summary>
    internal const string ClusterConnectionLabel = "default";

    public static async Task<StagingCopyReadiness> CheckAsync(
        ILogger logger, string tenantId, ICrateCopyClient? copyClient, CancellationToken cancellationToken)
    {
        if (copyClient is null)
        {
            return new StagingCopyReadiness(false, false, null,
                "This stream data repository does not support the native copy to a staging location.");
        }

        var options = new ArchiveCopyOptions();
        try
        {
            var filterSet = false;
            string? filter = null;
            var setting = await copyClient.ExecuteStatementAsync(tenantId, JobLogFilterGuard.ReadSetting, options.CommandTimeout, cancellationToken);
            if (setting.Rows.Count > 0 && setting.Rows[0].TryGetValue("jobs_log_filter", out var value))
            {
                filter = value as string;
                filterSet = JobLogFilterGuard.IsFiltered(filter);
            }

            string? version = null;
            try
            {
                version = await new StagingCopyRunner(logger, tenantId, copyClient).GetStorageVersionAsync(options, cancellationToken);
            }
            catch (Exception ex) when (ex is StreamDataCopyStatementException or StreamDataStatementTimeoutException)
            {
                // The version is informational; the filter decides.
            }

            var reason = filterSet
                ? null
                : $"The cluster setting {JobLogFilterGuard.SettingName} does not keep COPY statements out of sys.jobs_log (it is '{filter ?? "(not set)"}'); " +
                  $"set it, e.g. SET GLOBAL PERSISTENT \"{JobLogFilterGuard.SettingName}\" = '{JobLogFilterGuard.RecommendedFilter.Replace("'", "''", StringComparison.Ordinal)}'.";
            return new StagingCopyReadiness(true, filterSet, version, reason);
        }
        catch (Exception ex) when (ex is StreamDataCopyStatementException or StreamDataStatementTimeoutException)
        {
            return new StagingCopyReadiness(true, false, null, $"The storage could not be asked whether the native copy can run: {ex.Message}");
        }
    }
}
