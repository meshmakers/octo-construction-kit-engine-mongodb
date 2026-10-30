namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;

/// <summary>
///     One persisted row of the CK method handler registry snapshot (AB#5671): one handler instance for one method,
///     as a dispatcher last saw it. The row is data about a <b>service</b>, never about a tenant entity: it holds ids,
///     names, versions, times and the served tenant ids, and by construction no parameter value, token or user data.
/// </summary>
/// <remarks>
///     The snapshot is a <b>last seen</b> record, not a liveness claim. A reader treats a row as live only while
///     <see cref="ExpiresUtc" /> has not passed (the heartbeat TTL window) and <see cref="Withdrawn" /> is
///     <c>false</c>; every other row is history. Rows disappear by themselves at <see cref="RetainUntilUtc" />
///     (MongoDB TTL index).
/// </remarks>
public sealed class CkMethodHandlerSnapshotRecord
{
    /// <summary>Method key: the model-version-less, case-insensitive queue name of the method (part of the unique key).</summary>
    public string MethodKey { get; set; } = string.Empty;

    /// <summary>Handler instance id (part of the unique key).</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Qualified method id as announced.</summary>
    public string MethodId { get; set; } = string.Empty;

    /// <summary>Handler service name.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Informational handler service version.</summary>
    public string? Version { get; set; }

    /// <summary>Highest wire contract version the instance understands.</summary>
    public int ContractVersion { get; set; }

    /// <summary>Last announcement or withdrawal received (dispatcher clock, UTC).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>End of the heartbeat TTL window (UTC); the instance counts as live until then.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary><c>true</c> after a graceful withdrawal; such a row is never live.</summary>
    public bool Withdrawn { get; set; }

    /// <summary>
    ///     Timestamp of the withdrawal at the handler (its own clock), kept so an older announcement that arrives
    ///     after a restart cannot resurrect a stopped instance.
    /// </summary>
    public DateTime? WithdrawnAtSource { get; set; }

    /// <summary><c>true</c> when the entry was expired because the broker returned a mandatory send.</summary>
    public bool NoQueueBound { get; set; }

    /// <summary>Served tenant ids (<c>"*"</c> = all).</summary>
    public List<string> Tenants { get; set; } = [];

    /// <summary>The row is removed by the TTL index at this time (UTC).</summary>
    public DateTime RetainUntilUtc { get; set; }
}
