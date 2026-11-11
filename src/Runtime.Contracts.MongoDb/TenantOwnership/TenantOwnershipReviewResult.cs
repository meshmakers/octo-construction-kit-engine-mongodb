namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;

/// <summary>Why a tenant database of this instance is listed by the ownership review (AB#8796).</summary>
public enum TenantOwnershipIssue
{
    /// <summary>The database has no ownership marker and only this instance registers it (stampable).</summary>
    Unmarked,

    /// <summary>The database carries the marker of another instance.</summary>
    MarkedByOtherInstance,

    /// <summary>The database is registered in this and at least one other system database on the server.</summary>
    RegisteredByOtherInstance
}

/// <summary>A tenant database the ownership review lists (AB#8796).</summary>
/// <param name="TenantId">Normalized tenant id in this instance.</param>
/// <param name="DatabaseName">Normalized tenant database name.</param>
/// <param name="Issue">Why it is listed; a double registration wins over a foreign marker, which wins over no marker.</param>
/// <param name="MarkerOwnerSystemDatabaseName">The system database the marker names, or null without marker.</param>
/// <param name="RegisteringSystemDatabases">All system databases on the server that register the database.</param>
/// <param name="Stamped">True when this run wrote the marker of this instance.</param>
public sealed record TenantOwnershipReviewEntry(
    string TenantId,
    string DatabaseName,
    TenantOwnershipIssue Issue,
    string? MarkerOwnerSystemDatabaseName,
    IReadOnlyList<string> RegisteringSystemDatabases,
    bool Stamped);

/// <summary>Outcome of <see cref="ISystemContext.ReviewTenantOwnershipAsync" /> (AB#8796).</summary>
public sealed class TenantOwnershipReviewResult
{
    /// <summary>The system database (instance identity) of the instance that ran the review.</summary>
    public string SystemDatabaseName { get; init; } = string.Empty;

    /// <summary>True when the review ran with stamp; false for a read-only listing.</summary>
    public bool Stamp { get; init; }

    /// <summary>Tenant databases of this instance that were checked.</summary>
    public int Checked { get; init; }

    /// <summary>Databases that already carry the marker of this instance and are registered only here.</summary>
    public int Owned { get; init; }

    /// <summary>Registry rows whose database does not exist; they are skipped and never stamped.</summary>
    public int MissingDatabases { get; init; }

    /// <summary>The listed databases: no marker, a marker of another instance, or several registrations.</summary>
    public IReadOnlyList<TenantOwnershipReviewEntry> Entries { get; init; } = [];

    /// <summary>One-line human-readable summary.</summary>
    public string Summary =>
        $"{(Stamp ? "Stamp" : "List")}: {Checked} tenant database(s) checked, {Owned} owned, " +
        $"{Entries.Count(e => e.Stamped)} stamped, " +
        $"{Entries.Count(e => !e.Stamped && e.Issue == TenantOwnershipIssue.Unmarked)} unmarked, " +
        $"{Entries.Count(e => e.Issue == TenantOwnershipIssue.MarkedByOtherInstance)} marked by another instance, " +
        $"{Entries.Count(e => e.Issue == TenantOwnershipIssue.RegisteredByOtherInstance)} registered by another instance, " +
        $"{MissingDatabases} missing";
}
