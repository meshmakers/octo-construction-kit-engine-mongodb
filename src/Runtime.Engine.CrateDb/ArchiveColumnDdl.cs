namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pre-resolved column descriptor consumed by <see cref="ArchiveDdlGenerator"/>. The path is taken
/// straight from <c>CkArchiveColumn.Path</c> and used as-is for the column name (quoted with
/// embedded dots so nested paths like <c>sensor.reading.value</c> stay readable in DDL output).
/// </summary>
/// <param name="Path">
/// Attribute path from <c>CkArchiveColumn.Path</c>. Becomes the column name verbatim.
/// </param>
/// <param name="Type">Resolved CrateDB column type (scalar, array, object, array-of-object).</param>
/// <param name="Required">
/// When true the column is emitted with <c>NOT NULL</c>. For array-typed columns the constraint
/// applies to the array itself; element gaps inside the array are not constrained (concept D8).
/// </param>
/// <param name="Indexed">
/// When false the column is emitted with <c>INDEX OFF</c>; defaults true to match CrateDB's
/// standard indexing.
/// </param>
internal sealed record ArchiveColumnDdl(
    string Path,
    CrateColumnType Type,
    bool Required,
    bool Indexed);
