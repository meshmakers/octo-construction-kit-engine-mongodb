using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     Reads the numbers of one <see cref="AutoIncrementNumberFormat" /> and one fiscal year back into their counter
///     value (<see cref="AutoIncrementNumberFormat.CreateReader" />). <see cref="Prefix" /> is the literal text every
///     such number starts with (the text before the counter, the fiscal year included), so a query can find the numbers
///     of one fiscal year through an index on the number attribute.
/// </summary>
public sealed class AutoIncrementNumberReader
{
    private readonly AutoIncrementNumberFormat _format;
    private readonly int? _fiscalYear;
    private readonly Regex _pattern;

    internal AutoIncrementNumberReader(AutoIncrementNumberFormat format, int? fiscalYear, string prefix, Regex pattern)
    {
        _format = format;
        _fiscalYear = fiscalYear;
        Prefix = prefix;
        _pattern = pattern;
    }

    /// <summary>The text before the counter, e.g. <c>RE-2027-</c> for <c>RE-{yyyy}-{counter:D5}</c> and 2027.</summary>
    public string Prefix { get; }

    /// <summary>
    ///     The counter value of <paramref name="number" />, when it is a number this format writes for this fiscal year:
    ///     the text matches the format and formatting the value gives the same text again (so <c>RE-2027-007</c> is
    ///     not read for <c>{counter:D5}</c>, and <c>RE-2026-00007</c> not for 2027).
    /// </summary>
    public bool TryRead(string? number, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(number))
        {
            return false;
        }

        var match = _pattern.Match(number);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        string formatted;
        try
        {
            formatted = _format.Format(parsed, _fiscalYear);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(formatted, number, StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
