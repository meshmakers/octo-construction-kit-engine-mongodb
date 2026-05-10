namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

internal static class Queries
{
    /// <summary>
    /// Drop-table template used for both archive tables and any leftover legacy table when the
    /// management client is asked to remove storage for a tenant. CrateDB has no <c>DROP SCHEMA</c>;
    /// the schema goes away once its last table is dropped.
    /// </summary>
    public const string DeleteTableIfExists = "drop table if exists {0};";

    /// <summary>
    /// CrateDB applies inserts asynchronously to the storage layer; readers see them only after
    /// the next refresh (default ~1s). Production code that needs strict read-after-write
    /// consistency must call this explicitly after a bulk insert. Concept §15.
    /// </summary>
    public const string RefreshTable = "REFRESH TABLE {0};";
}
