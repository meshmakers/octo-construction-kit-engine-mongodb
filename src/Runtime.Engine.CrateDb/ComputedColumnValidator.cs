using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Schema-time validation for computed columns (concept §9). Runs before an archive table is
/// provisioned. Enforces: mutual exclusivity (a column is either ingested via <c>Path</c> or
/// computed via <c>Formula</c>, never both), computed columns are nullable, a computed column has a
/// <c>Name</c> and a <c>ResultType</c>, the formula is syntactically valid and references only
/// existing columns of the same archive, and the computed-column reference graph is acyclic.
/// <para>
/// References resolve against the <b>physical</b> column names (lower-cased, dot-stripped by
/// <see cref="ColumnNameMapper"/>) — the same names the ingest path binds (concept §5).
/// </para>
/// </summary>
internal static class ComputedColumnValidator
{
    public static void Validate(
        OctoObjectId archiveRtId,
        IReadOnlyList<CkArchiveColumnSpec> columns,
        IFormulaEngine formulaEngine)
    {
        List<(CkArchiveColumnSpec Spec, string PhysicalName)>? computed = null;
        foreach (var c in columns)
        {
            if (!c.IsComputed)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(c.Path))
            {
                throw new ComputedColumnInvalidException(archiveRtId, c.Name,
                    "a computed column must not also set Path.");
            }

            if (c.Required)
            {
                throw new ComputedColumnInvalidException(archiveRtId, c.Name,
                    "a computed column must be nullable (Required must be false).");
            }

            if (string.IsNullOrWhiteSpace(c.Name))
            {
                throw new ComputedColumnInvalidException(archiveRtId, null,
                    "a computed column requires a Name.");
            }

            if (c.ResultType is null)
            {
                throw new ComputedColumnInvalidException(archiveRtId, c.Name,
                    "a computed column requires a ResultType.");
            }

            computed ??= new List<(CkArchiveColumnSpec, string)>();
            computed.Add((c, ColumnNameMapper.PathToColumnName(c.Name!)));
        }

        if (computed is null)
        {
            return;
        }

        // Physical names of every column (ingested + computed) — the reference universe a formula
        // may draw from.
        var allPhysical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in columns)
        {
            if (c.IsComputed)
            {
                if (!string.IsNullOrWhiteSpace(c.Name))
                {
                    allPhysical.Add(ColumnNameMapper.PathToColumnName(c.Name!));
                }
            }
            else if (!string.IsNullOrWhiteSpace(c.Path))
            {
                allPhysical.Add(ColumnNameMapper.PathToColumnName(c.Path));
            }
        }

        // Syntax + reference resolution: a formula may reference any column except itself.
        foreach (var (spec, physical) in computed)
        {
            var argNames = allPhysical.Where(n => !string.Equals(n, physical, StringComparison.Ordinal)).ToList();
            var result = formulaEngine.CheckSyntax(spec.Formula!, argNames);
            if (!result.IsValid)
            {
                throw new ComputedColumnInvalidException(archiveRtId, spec.Name,
                    result.Error ?? "the formula is not valid.");
            }
        }

        DetectCycles(archiveRtId, computed);
    }

    private static void DetectCycles(
        OctoObjectId archiveRtId,
        List<(CkArchiveColumnSpec Spec, string PhysicalName)> computed)
    {
        var computedNames = new HashSet<string>(computed.Select(c => c.PhysicalName), StringComparer.Ordinal);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var columnNameByPhysical = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (spec, physical) in computed)
        {
            columnNameByPhysical[physical] = spec.Name;
            adjacency[physical] = computedNames
                .Where(n => !string.Equals(n, physical, StringComparison.Ordinal)
                            && ReferencesToken(spec.Formula!, n))
                .ToList();
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unvisited, 1 in-stack, 2 done
        foreach (var node in adjacency.Keys)
        {
            if (HasCycle(node, adjacency, state, out var cycleNode))
            {
                throw new ComputedColumnInvalidException(archiveRtId, columnNameByPhysical[cycleNode],
                    "cyclic reference between computed columns.");
            }
        }
    }

    private static bool HasCycle(
        string node,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, int> state,
        out string cycleNode)
    {
        cycleNode = node;
        state.TryGetValue(node, out var s);
        if (s == 1)
        {
            return true; // back-edge → cycle
        }

        if (s == 2)
        {
            return false;
        }

        state[node] = 1;
        foreach (var next in adjacency[node])
        {
            if (HasCycle(next, adjacency, state, out cycleNode))
            {
                return true;
            }
        }

        state[node] = 2;
        return false;
    }

    /// <summary>Whole-word (identifier-boundary) check for <paramref name="token"/> in a formula.</summary>
    private static bool ReferencesToken(string formula, string token)
    {
        var idx = 0;
        while ((idx = formula.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !IsIdentifierChar(formula[idx - 1]);
            var afterPos = idx + token.Length;
            var afterOk = afterPos >= formula.Length || !IsIdentifierChar(formula[afterPos]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            idx = afterPos;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
