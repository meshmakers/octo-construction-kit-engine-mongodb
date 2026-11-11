using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     The ownership list/stamp operation (AB#8796) over the tenant databases of one instance, see
///     <c>ISystemContext.ReviewTenantOwnershipAsync</c>. Stamps only databases that are unambiguous:
///     no marker and registered in no other system database on the server.
/// </summary>
internal sealed class TenantOwnershipReview
{
    private readonly TenantOwnershipStore _store;
    private readonly TenantDatabaseRegistrationLookup _lookup;
    private readonly ILogger<TenantOwnershipReview> _logger;

    public TenantOwnershipReview(TenantOwnershipStore store, TenantDatabaseRegistrationLookup lookup,
        ILogger<TenantOwnershipReview> logger)
    {
        _store = store;
        _lookup = lookup;
        _logger = logger;
    }

    /// <param name="ownSystemDatabaseName">Normalized system database of this instance (its identity).</param>
    /// <param name="tenants">This instance's registry rows with the existence of each database.</param>
    /// <param name="stamp">True to stamp the unambiguous unmarked databases.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<TenantOwnershipReviewResult> RunAsync(string ownSystemDatabaseName,
        IReadOnlyList<(string TenantId, string DatabaseName, bool DatabaseExists)> tenants, bool stamp,
        CancellationToken cancellationToken = default)
    {
        var registrations = await _lookup.GetRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<TenantOwnershipReviewEntry>();
        var owned = 0;
        var missing = 0;
        var checkedCount = 0;

        foreach (var (tenantId, databaseName, databaseExists) in tenants)
        {
            if (!databaseExists)
            {
                missing++;
                continue;
            }

            checkedCount++;
            var marker = await _store.GetAsync(databaseName, cancellationToken).ConfigureAwait(false);
            var registering = registrations.GetRegisteringSystemDatabases(databaseName);
            var registeredElsewhere = registering.Any(r => !string.Equals(r, ownSystemDatabaseName, StringComparison.Ordinal));
            var markedElsewhere = marker != null &&
                                  !string.Equals(marker.OwnerSystemDatabaseName, ownSystemDatabaseName, StringComparison.Ordinal);

            TenantOwnershipIssue issue;
            if (registeredElsewhere)
            {
                issue = TenantOwnershipIssue.RegisteredByOtherInstance;
            }
            else if (markedElsewhere)
            {
                issue = TenantOwnershipIssue.MarkedByOtherInstance;
            }
            else if (marker == null)
            {
                issue = TenantOwnershipIssue.Unmarked;
            }
            else
            {
                owned++;
                continue;
            }

            var stamped = false;
            var markerOwner = marker?.OwnerSystemDatabaseName;
            if (stamp && issue == TenantOwnershipIssue.Unmarked)
            {
                // Insert-if-absent: another instance stamping in between wins and is reported as such.
                var after = await _store.StampIfAbsentAndGetAsync(databaseName, tenantId, ownSystemDatabaseName,
                    cancellationToken).ConfigureAwait(false);
                markerOwner = after.OwnerSystemDatabaseName;
                stamped = string.Equals(markerOwner, ownSystemDatabaseName, StringComparison.Ordinal);
                if (!stamped)
                {
                    issue = TenantOwnershipIssue.MarkedByOtherInstance;
                }
                else
                {
                    _logger.LogInformation(
                        "Stamped the ownership marker of instance '{SystemDatabaseName}' on tenant database " +
                        "'{DatabaseName}' (tenant '{TenantId}')", ownSystemDatabaseName, databaseName, tenantId);
                }
            }

            if (issue != TenantOwnershipIssue.Unmarked)
            {
                _logger.LogWarning(
                    "Tenant database '{DatabaseName}' (tenant '{TenantId}') is not owned unambiguously: {Issue}, " +
                    "marker owner '{MarkerOwner}', registered in {RegisteringSystemDatabases}; left untouched",
                    databaseName, tenantId, issue, markerOwner ?? "(none)", string.Join(", ", registering));
            }

            entries.Add(new TenantOwnershipReviewEntry(tenantId, databaseName, issue, markerOwner, registering, stamped));
        }

        return new TenantOwnershipReviewResult
        {
            SystemDatabaseName = ownSystemDatabaseName,
            Stamp = stamp,
            Checked = checkedCount,
            Owned = owned,
            MissingDatabases = missing,
            Entries = entries.OrderBy(e => e.TenantId, StringComparer.Ordinal).ToList()
        };
    }
}
