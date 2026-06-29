using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Physical CrateDB column-name derivation for computed columns (AB#4189 Phase 7). A computed
/// column's base physical name is <see cref="ColumnNameMapper.PathToColumnName"/> applied to its
/// <c>Name</c>; a formula change moves the active values into a versioned physical column
/// <c>{base}__v{N}</c> and flips the version pointer (<see cref="CkArchiveColumnSpec.ComputedVersion"/>)
/// atomically, so the old physical column is left as an orphan and the new one becomes authoritative.
/// </summary>
internal static class ComputedColumnNaming
{
    /// <summary>The base (version-0) physical column name derived from the column's <c>Name</c>.</summary>
    public static string Base(CkArchiveColumnSpec column) => ColumnNameMapper.PathToColumnName(column.Name!);

    /// <summary>The currently-active physical column name: base for version 0, <c>{base}__v{N}</c> otherwise.</summary>
    public static string Active(CkArchiveColumnSpec column) => WithVersion(Base(column), column.ComputedVersion);

    /// <summary>The next (pending) physical column name a formula change backfills into.</summary>
    public static string Pending(CkArchiveColumnSpec column) => WithVersion(Base(column), column.ComputedVersion + 1);

    /// <summary>Applies the version suffix: no suffix for version 0, <c>{base}__v{version}</c> otherwise.</summary>
    public static string WithVersion(string baseName, int version) =>
        version > 0 ? $"{baseName}__v{version}" : baseName;
}
