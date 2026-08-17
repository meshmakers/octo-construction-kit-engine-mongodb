namespace Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;

/// <summary>
///     Declared display rules of a CK type (as persisted on the CkType document).
/// </summary>
internal readonly record struct DeclaredDisplayRules(string? DisplayNameRule, string? DisplayDescriptionRule)
{
    internal bool HasAnyRule => !string.IsNullOrWhiteSpace(DisplayNameRule) ||
                                !string.IsNullOrWhiteSpace(DisplayDescriptionRule);
}

/// <summary>
///     Pure diff of declared display rules before/after a CK model import (AB#4812). A change on a
///     declaring type is swept polymorphically from that type, which also covers every derived type
///     inheriting the rule — so diffing declared (not effective) rules is sufficient.
/// </summary>
internal static class DisplayRuleChangeDetector
{
    /// <summary>
    ///     Returns the CK type ids (full names) whose declared rules changed and that still exist
    ///     after the import (a type that disappeared has no entities to sweep under its own id —
    ///     entity migration is the model migration's concern). A type that is new after the import
    ///     is only reported when it declares a rule (it may have been re-imported after a temporary
    ///     removal, so existing entities cannot be ruled out).
    /// </summary>
    internal static IReadOnlyCollection<string> GetChangedTypeIds(
        IReadOnlyDictionary<string, DeclaredDisplayRules> beforeImport,
        IReadOnlyDictionary<string, DeclaredDisplayRules> afterImport)
    {
        var changed = new List<string>();
        foreach (var pair in afterImport)
        {
            if (beforeImport.TryGetValue(pair.Key, out var oldRules))
            {
                if (!RulesEqual(oldRules, pair.Value))
                {
                    changed.Add(pair.Key);
                }
            }
            else if (pair.Value.HasAnyRule)
            {
                changed.Add(pair.Key);
            }
        }

        return changed;
    }

    private static bool RulesEqual(DeclaredDisplayRules a, DeclaredDisplayRules b)
    {
        return NormalizeRule(a.DisplayNameRule) == NormalizeRule(b.DisplayNameRule) &&
               NormalizeRule(a.DisplayDescriptionRule) == NormalizeRule(b.DisplayDescriptionRule);
    }

    private static string? NormalizeRule(string? rule)
    {
        return string.IsNullOrWhiteSpace(rule) ? null : rule!.Trim();
    }
}
