using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Translates a computed-column formula written in the archive's <b>logical</b> column vocabulary —
/// the CK attribute paths the Refinery Studio lists and the GraphQL surface uses, e.g.
/// <c>Amount.Value</c> — into the <b>physical</b> form the evaluation path binds, e.g.
/// <c>amountvalue</c> (concept §5, "Phase 4").
/// <para>
/// Why a translation is needed at all: the formula is not evaluated by CrateDB but in .NET via
/// mXparser, which binds each operand as an <c>Argument</c> whose name cannot contain a dot. The row
/// dictionaries the evaluator reads are keyed by the physical name. So the dotted, mixed-case form a
/// user sees can never reach mXparser as-is — it has to be rewritten first.
/// </para>
/// <para>
/// Why translating (rather than teaching the evaluator) is the right layer: everywhere else in the
/// platform "the API surface still carries the original PascalCase / dotted form — only the physical
/// CrateDB column is lower-case" (see <see cref="ColumnNameMapper"/>). Formulas were the one place
/// that leaked the physical form to the user, with no way to discover it.
/// </para>
/// </summary>
internal static class ComputedColumnFormulaRewriter
{
    /// <summary>
    /// Every column of the archive by the name a formula may write, mapped to its physical column
    /// name. Ingested columns are addressed by <c>Path</c>, computed ones by <c>Name</c> — the same
    /// universe <see cref="ComputedColumnValidator" /> resolves references against, which is why that
    /// validator builds its set from this map rather than repeating the rule.
    /// <para>
    /// Keyed case-insensitively, matching the query side's <c>StreamDataFieldResolver</c>: a caller
    /// who writes <c>amount.value</c> means the same column as one who writes <c>Amount.Value</c>.
    /// </para>
    /// </summary>
    internal static Dictionary<string, string> BuildNameMap(IReadOnlyList<CkArchiveColumnSpec> columns)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in columns)
        {
            var logical = c.IsComputed ? c.Name : c.Path;
            if (string.IsNullOrWhiteSpace(logical))
            {
                continue;
            }

            // Last one wins on a collision. Two CK paths can map to the same physical column
            // (Amount.Value and AmountValue both give amountvalue), but then the archive itself
            // carries two columns with one physical name, which activation rejects — so within a
            // valid archive this map is effectively injective.
            //
            // A computed column maps to its BASE name, deliberately not ComputedColumnNaming.Active:
            // the active name carries the version suffix after a formula change, and writing that
            // into a stored formula would go stale on the next change (__v2 → __v3) while the formula
            // still says __v2. The consequence is a pre-existing gap this map neither causes nor
            // cures: a formula referencing a computed column that has been re-formulated binds
            // nothing at evaluation, because ApplyComputedColumns keys the row by the active name.
            // Fixing that belongs in the evaluation path — expose the value under the logical name
            // too — not here by baking a version into the text.
            map[logical!] = ColumnNameMapper.PathToColumnName(logical!);
        }

        return map;
    }

    /// <summary>
    /// Rewrites every known column reference in <paramref name="formula" /> to its physical name and
    /// leaves everything else untouched. An unknown name is deliberately left as written so the
    /// validator can reject it by the spelling the caller used, rather than by a half-rewritten form.
    /// </summary>
    internal static string ToPhysicalForm(string? formula, IReadOnlyList<CkArchiveColumnSpec> columns)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            return formula ?? string.Empty;
        }

        var map = BuildNameMap(columns);
        return ToPhysicalForm(formula!, map);
    }

    /// <summary>
    /// Overload for callers that already built the map (the validator resolves several formulas
    /// against one archive).
    /// </summary>
    internal static string ToPhysicalForm(string formula, Dictionary<string, string> nameMap)
    {
        var result = new System.Text.StringBuilder(formula.Length);
        var i = 0;

        while (i < formula.Length)
        {
            var c = formula[i];

            // A numeric literal is skipped whole, dots and exponent included. Identifier runs below
            // start only on a letter or underscore, so "1.5" could never be mistaken for a name —
            // but "1.5e3" would otherwise start a run at 'e', and a column named "e3" would then be
            // substituted into the middle of a number.
            if (char.IsDigit(c))
            {
                while (i < formula.Length && (char.IsDigit(formula[i]) || formula[i] == '.'))
                {
                    result.Append(formula[i++]);
                }

                if (i < formula.Length && (formula[i] == 'e' || formula[i] == 'E'))
                {
                    result.Append(formula[i++]);
                    if (i < formula.Length && (formula[i] == '+' || formula[i] == '-'))
                    {
                        result.Append(formula[i++]);
                    }
                }

                continue;
            }

            if (!IsRunStart(c))
            {
                result.Append(c);
                i++;
                continue;
            }

            // An identifier run may contain dots, because that is what a CK attribute path looks
            // like. A trailing dot belongs to no name and is handed back to the output.
            var start = i;
            i++;
            while (i < formula.Length && IsRunChar(formula[i]))
            {
                i++;
            }

            var end = i;
            while (end > start && formula[end - 1] == '.')
            {
                end--;
            }

            var run = formula[start..end];

            // The whole run must match a name. Matching a mere prefix would rewrite "Amount.Value"
            // to "amount.Value" on an archive that has a column "Amount" but no "Amount.Value" —
            // broken output instead of an honest "unknown column" from the validator. It also makes
            // longest-match fall out for free: the run *is* the longest candidate.
            result.Append(nameMap.TryGetValue(run, out var physical) ? physical : run);
            result.Append(formula[end..i]);
        }

        return result.ToString();
    }

    private static bool IsRunStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsRunChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}
