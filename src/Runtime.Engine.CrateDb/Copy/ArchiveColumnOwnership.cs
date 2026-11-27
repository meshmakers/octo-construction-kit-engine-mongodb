using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// Which physical columns of an archive table its definition owns, and which of the others are left over from
/// computed columns: what a dump leaves out besides the Hidden columns.
/// </summary>
/// <remarks>
/// <para>The physical table of an archive only grows. Removing a computed column takes it out of the definition and
/// leaves its physical column - with every <c>{base}__v{N}</c> version of earlier formula changes - in the table; a
/// formula change of a computed column that stays leaves the earlier version behind. The restore provisions the table
/// from the definition, so such a column has no place to load into. Its values are derived: they were computed from
/// the stored columns with a formula that no longer exists (or no longer is the column's formula), so a dump leaves
/// them out instead of carrying them into a load that refuses them.</para>
/// <para>A column is a leftover only on positive evidence, every other column the definition does not own stays in
/// the dump (and the restore refuses it, as before): it is not a standard or platform column, its type is one a
/// computed column has (<c>boolean</c>, <c>integer</c>, <c>bigint</c>, <c>double precision</c>,
/// <c>timestamp with time zone</c>), and either it carries the name of a computed column of the definition (its base or
/// a stored version other than the active and the pending one), or it carries a versioned name (<c>{base}__v{N}</c>,
/// or the base of such a column in the same table), or the archive's CK type is known and no attribute path of it maps
/// to the name. A plain column named like an attribute path of the type is ingested data the definition no longer
/// declares, never a leftover - unless a computed column of the definition explains it as one of its stored versions.</para>
/// </remarks>
internal sealed class ArchiveColumnOwnership
{
    private static readonly HashSet<string> ComputedDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "boolean", "integer", "bigint", "double precision", "timestamp with time zone"
    };

    private readonly HashSet<string> _owned;
    private readonly IReadOnlyList<(string Base, IReadOnlySet<string> Current)> _computed;
    private readonly IReadOnlySet<string>? _attributeColumns;

    /// <param name="owned">Physical names the definition owns: standard and platform columns, the ingested (or
    /// aggregate) columns and the active and pending names of the computed columns.</param>
    /// <param name="computed">Per computed column of the definition: its base name and its current physical names
    /// (active, and pending during a formula change).</param>
    /// <param name="attributeColumns">The physical names every attribute path of the archive's CK type maps to;
    /// <c>null</c> when the type cannot be read (then only the naming evidence counts).</param>
    public ArchiveColumnOwnership(IEnumerable<string> owned,
        IReadOnlyList<(string Base, IReadOnlySet<string> Current)> computed, IReadOnlySet<string>? attributeColumns)
    {
        _owned = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        _computed = computed;
        _attributeColumns = attributeColumns;
    }

    /// <summary>
    /// The top-level columns of <paramref name="tableColumns"/> (an <c>information_schema</c> description, sub-columns
    /// included) that are left over from computed columns, by name.
    /// </summary>
    public IReadOnlySet<string> FindLeftovers(IReadOnlyList<ArchiveCopyColumn> tableColumns)
    {
        // A sub-column (obj['member']) belongs to an object column, never to a computed one.
        var topLevel = tableColumns.Where(c => StagingCopyRunner.TopLevelColumn(c.Name) == c.Name).ToList();
        var names = new HashSet<string>(topLevel.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var leftovers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in topLevel)
        {
            if (IsLeftover(column, names))
            {
                leftovers.Add(column.Name);
            }
        }

        return leftovers;
    }

    private bool IsLeftover(ArchiveCopyColumn column, HashSet<string> tableColumnNames)
    {
        var name = column.Name;
        if (_owned.Contains(name) || !ComputedDataTypes.Contains(column.DataType.Trim()))
        {
            return false;
        }

        // A stored version of a computed column the definition still has (its base or {base}__v{N}): the definition
        // itself explains the column, also when the computed column carries the name of an attribute of the type.
        if (_computed.Any(c => !c.Current.Contains(name) &&
                               (string.Equals(name, c.Base, StringComparison.OrdinalIgnoreCase) ||
                                CrateDbStreamDataRepository.IsStoredVersionOf(name, c.Base))))
        {
            return true;
        }

        if (_attributeColumns is { } attributes && attributes.Contains(name))
        {
            return false; // ingested data of the type that the definition no longer declares
        }

        // A versioned name is made only by a formula change: {base}__v{N}, and {base} when one of its versions is there.
        if (VersionedBase(name) is not null ||
            tableColumnNames.Any(other => CrateDbStreamDataRepository.IsStoredVersionOf(other, name)))
        {
            return true;
        }

        // Neither the definition nor the CK type knows the column: only a removed computed column leaves one behind.
        return _attributeColumns is not null;
    }

    /// <summary>The base of <c>{base}__v{N}</c>; <c>null</c> for a name without a version suffix.</summary>
    internal static string? VersionedBase(string name)
    {
        var at = name.LastIndexOf("__v", StringComparison.OrdinalIgnoreCase);
        return at > 0 && CrateDbStreamDataRepository.IsStoredVersionOf(name, name[..at]) ? name[..at] : null;
    }
}
