using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     Process-wide record of the ownership conflicts seen on tenant resolve (AB#8796), keyed by
///     normalized tenant id, and of a failed system-database marker stamp (AB#8672). Feeds the service health
///     check (<c>Degraded</c>) and decides that the resolve warning is logged once per process and tenant.
/// </summary>
/// <remarks>
///     A recorded conflict is rechecked (<see cref="RecheckAsync" />) at most once per <see cref="RecheckInterval" />:
///     by the health check and by every resolve of a conflicting tenant. The conflict is forgotten once the database
///     no longer carries another instance's marker - the other instance detached it (the marker is gone; the
///     recheck then claims the database for this instance, like the lazy claim of a resolve) or the marker names this
///     instance. While another instance's marker stays, the conflict stays and the health stays <c>Degraded</c>.
/// </remarks>
internal sealed class TenantOwnershipConflictTracker : ITenantOwnershipConflicts
{
    /// <summary>The default bound on how often the recorded conflicts are rechecked against their databases.</summary>
    public static readonly TimeSpan DefaultRecheckInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, TenantOwnershipConflict> _conflicts =
        new(StringComparer.Ordinal);

    private readonly TenantOwnershipStore? _store;
    private readonly string? _ownerSystemDatabaseName;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantOwnershipConflictTracker>? _logger;
    private readonly SemaphoreSlim _recheckLock = new(1, 1);
    private DateTimeOffset _lastRecheck = DateTimeOffset.MinValue;

    public TenantOwnershipConflictTracker(TenantOwnershipStore store, IOptions<OctoSystemConfiguration> systemConfiguration,
        ILogger<TenantOwnershipConflictTracker> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _ownerSystemDatabaseName = systemConfiguration.Value.SystemDatabaseName.Trim().ToLowerInvariant();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     How often at most the recorded conflicts are rechecked (default <see cref="DefaultRecheckInterval" />).
    ///     Settable for tests.
    /// </summary>
    internal TimeSpan RecheckInterval { get; set; } = DefaultRecheckInterval;

    public IReadOnlyList<TenantOwnershipConflict> Current =>
        _conflicts.Values.OrderBy(c => c.TenantId, StringComparer.Ordinal).ToList();

    /// <summary>Whether a conflict is recorded for the tenant.</summary>
    public bool HasConflict(string tenantId) => _conflicts.ContainsKey(tenantId);

    /// <summary>
    ///     Records a conflict. Returns true when it is new for this process (or names another owner or
    ///     database than the recorded one), so the caller logs the warning exactly then.
    /// </summary>
    public bool Report(string tenantId, string databaseName, string ownerSystemDatabaseName)
    {
        var conflict = new TenantOwnershipConflict(tenantId, databaseName, ownerSystemDatabaseName,
            _timeProvider.GetUtcNow().UtcDateTime);
        var isNew = true;
        _conflicts.AddOrUpdate(tenantId, conflict, (_, existing) =>
        {
            if (existing.DatabaseName == databaseName && existing.OwnerSystemDatabaseName == ownerSystemDatabaseName)
            {
                isNew = false;
                return existing;
            }

            return conflict;
        });
        return isNew;
    }

    /// <summary>Forgets the conflict of a tenant (resolved without conflict, detached or deleted).</summary>
    public void Clear(string tenantId) => _conflicts.TryRemove(tenantId, out _);

    /// <inheritdoc />
    public async Task RecheckAsync(CancellationToken cancellationToken = default)
    {
        if (_store == null || _ownerSystemDatabaseName == null || _conflicts.IsEmpty)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (now - _lastRecheck < RecheckInterval || !await _recheckLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            _lastRecheck = now;
            foreach (var conflict in _conflicts.Values.ToList())
            {
                try
                {
                    // Insert-if-absent, like the lazy claim of a resolve: a marker the other instance removed is
                    // claimed by this instance, which registers the tenant; another instance's marker is never touched.
                    var marker = await _store.StampIfAbsentAndGetAsync(conflict.DatabaseName, conflict.TenantId,
                        _ownerSystemDatabaseName, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(marker.OwnerSystemDatabaseName, _ownerSystemDatabaseName, StringComparison.Ordinal))
                    {
                        _conflicts.TryRemove(new KeyValuePair<string, TenantOwnershipConflict>(conflict.TenantId, conflict));
                        _logger?.LogInformation(
                            "The ownership conflict of tenant '{TenantId}' (database '{DatabaseName}') is resolved: " +
                            "the database is owned by this instance now", conflict.TenantId, conflict.DatabaseName);
                    }
                    else if (!string.Equals(marker.OwnerSystemDatabaseName, conflict.OwnerSystemDatabaseName,
                                 StringComparison.Ordinal))
                    {
                        Report(conflict.TenantId, conflict.DatabaseName, marker.OwnerSystemDatabaseName);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The conflict stays recorded; the next recheck tries again.
                    _logger?.LogDebug(ex, "Could not recheck the ownership conflict of tenant '{TenantId}'",
                        conflict.TenantId);
                }
            }
        }
        finally
        {
            _recheckLock.Release();
        }
    }

    private volatile string? _systemDatabaseMarkerProblem;

    /// <inheritdoc />
    public string? SystemDatabaseMarkerProblem => _systemDatabaseMarkerProblem;

    /// <summary>Records why the system-database marker could not be stamped (AB#8672).</summary>
    public void ReportSystemDatabaseMarkerProblem(string reason) => _systemDatabaseMarkerProblem = reason;

    /// <summary>The system-database marker is stamped: forgets an earlier failure.</summary>
    public void ClearSystemDatabaseMarkerProblem() => _systemDatabaseMarkerProblem = null;
}
