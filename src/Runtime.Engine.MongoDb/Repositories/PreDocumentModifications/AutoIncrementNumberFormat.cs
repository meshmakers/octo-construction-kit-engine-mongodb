using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     The <c>Format</c> of a <c>System/AutoIncrement</c> number range (AB#9754, design note 1.4). A format that contains
///     one of the tokens <c>{yyyy}</c>, <c>{yy}</c>, <c>{counter}</c> or <c>{counter:D&lt;n&gt;}</c> is a token format
///     (e.g. <c>RE-{yyyy}-{counter:D5}</c>): <c>{yyyy}</c> / <c>{yy}</c> are the fiscal year with four / two digits,
///     <c>{counter:D5}</c> the zero-padded counter, every other character is literal text. Any other format is a legacy
///     format and goes to <c>string.Format</c> with the counter as <c>{0}</c>, exactly as before System 2.9.0.
/// </summary>
public sealed partial class AutoIncrementNumberFormat
{
    /// <summary>Most digits <c>{counter:D&lt;n&gt;}</c> may ask for (a long has 19).</summary>
    public const int MaxCounterDigits = 18;

    private readonly IReadOnlyList<Part>? _parts;
    private readonly string? _legacyFormat;

    private AutoIncrementNumberFormat(string? legacyFormat, IReadOnlyList<Part>? parts)
    {
        _legacyFormat = legacyFormat;
        _parts = parts;
    }

    /// <summary>True when the range has a format (token or legacy); without one the plain number is written.</summary>
    public bool IsSet => _parts != null || _legacyFormat != null;

    /// <summary>True for a token format, false for a legacy <c>string.Format</c> format or no format.</summary>
    public bool IsTokenFormat => _parts != null;

    /// <summary>True when the format shows the fiscal year (<c>{yyyy}</c> or <c>{yy}</c>).</summary>
    public bool UsesFiscalYear => _parts?.Any(p => p.Kind is PartKind.Year4 or PartKind.Year2) == true;

    /// <summary>True when the format contains one of the tokens and is therefore read as a token format.</summary>
    public static bool IsTokenFormatString(string? format) =>
        !string.IsNullOrEmpty(format) && TokenRegex().IsMatch(format);

    /// <summary>
    ///     Parses <paramref name="format" />. A token format with an unknown <c>{...}</c> placeholder or an unclosed
    ///     brace is refused (<paramref name="error" /> says why), because a mix of tokens and <c>string.Format</c>
    ///     placeholders has no meaning; so is a token format without <c>{counter}</c>, because every number would be
    ///     the same text. A legacy format is formatted once with a trial number (AB#9755): one that
    ///     <c>string.Format</c> cannot format (e.g. <c>X-{counter:Dx}</c>, an unclosed brace, a placeholder other than
    ///     <c>{0}</c>) is refused, because it would fail at every allocation. Whether <c>string.Format</c> accepts a
    ///     format does not depend on the number, so every format that numbered before still does.
    /// </summary>
    public static bool TryParse(string? format, out AutoIncrementNumberFormat result, out string? error)
    {
        error = null;
        if (!IsTokenFormatString(format))
        {
            if (string.IsNullOrEmpty(format))
            {
                result = Empty;
                return true;
            }

            try
            {
                _ = string.Format(CultureInfo.InvariantCulture, format, 1L);
            }
            catch (FormatException ex)
            {
                error = $"string.Format cannot format it ({ex.Message.TrimEnd('.')}); use {{0}} or {{0:D<n>}} for " +
                        "the counter, or a token format such as {counter:D5}";
                result = Empty;
                return false;
            }

            result = new AutoIncrementNumberFormat(format, null);
            return true;
        }

        var parts = new List<Part>();
        var literal = new StringBuilder();
        var position = 0;
        while (position < format!.Length)
        {
            var open = format.IndexOf('{', position);
            var close = format.IndexOf('}', position);
            if (close >= 0 && (open < 0 || close < open))
            {
                error = $"unexpected '}}' at position {close}";
                result = Empty;
                return false;
            }

            if (open < 0)
            {
                literal.Append(format, position, format.Length - position);
                break;
            }

            literal.Append(format, position, open - position);
            var end = format.IndexOf('}', open);
            if (end < 0)
            {
                error = $"unclosed '{{' at position {open}";
                result = Empty;
                return false;
            }

            var token = format.Substring(open + 1, end - open - 1);
            if (!TryParseToken(token, out var part))
            {
                error = $"unknown token '{{{token}}}'; a token format knows {{yyyy}}, {{yy}}, {{counter}} and " +
                        $"{{counter:D<n>}} with n from 1 to {MaxCounterDigits}";
                result = Empty;
                return false;
            }

            if (literal.Length > 0)
            {
                parts.Add(new Part(PartKind.Literal, literal.ToString(), 0));
                literal.Clear();
            }

            parts.Add(part);
            position = end + 1;
        }

        if (literal.Length > 0)
        {
            parts.Add(new Part(PartKind.Literal, literal.ToString(), 0));
        }

        // Without the counter every number of a period would be the same text, i.e. a duplicate.
        if (parts.All(p => p.Kind != PartKind.Counter))
        {
            error = "a token format needs the counter ({counter} or {counter:D<n>}); without it every number " +
                    "would be the same";
            result = Empty;
            return false;
        }

        result = new AutoIncrementNumberFormat(null, parts);
        return true;
    }

    /// <summary>
    ///     The text of number <paramref name="value" />. <paramref name="fiscalYear" /> is needed only when the format
    ///     shows the fiscal year (<see cref="UsesFiscalYear" />).
    /// </summary>
    public string Format(long value, int? fiscalYear)
    {
        if (_parts == null)
        {
            return _legacyFormat == null
                ? value.ToString(CultureInfo.InvariantCulture)
                : string.Format(_legacyFormat, value);
        }

        var sb = new StringBuilder();
        foreach (var part in _parts)
        {
            switch (part.Kind)
            {
                case PartKind.Literal:
                    sb.Append(part.Text);
                    break;
                case PartKind.Year4:
                    sb.Append(RequireFiscalYear(fiscalYear).ToString("D4", CultureInfo.InvariantCulture));
                    break;
                case PartKind.Year2:
                    sb.Append((RequireFiscalYear(fiscalYear) % 100).ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case PartKind.Counter:
                    sb.Append(part.Digits > 0
                        ? value.ToString("D" + part.Digits.ToString(CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture)
                        : value.ToString(CultureInfo.InvariantCulture));
                    break;
            }
        }

        return sb.ToString();
    }

    private static int RequireFiscalYear(int? fiscalYear) =>
        fiscalYear ?? throw new InvalidOperationException(
            "The number format shows the fiscal year, but the range has no fiscal-year period.");

    private static bool TryParseToken(string token, out Part part)
    {
        switch (token)
        {
            case "yyyy":
                part = new Part(PartKind.Year4, null, 0);
                return true;
            case "yy":
                part = new Part(PartKind.Year2, null, 0);
                return true;
            case "counter":
                part = new Part(PartKind.Counter, null, 0);
                return true;
        }

        var match = CounterDigitsRegex().Match(token);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var digits) && digits is >= 1 and <= MaxCounterDigits)
        {
            part = new Part(PartKind.Counter, null, digits);
            return true;
        }

        part = new Part(PartKind.Literal, null, 0);
        return false;
    }

    private static readonly AutoIncrementNumberFormat Empty = new(null, null);

    [GeneratedRegex(@"\{(yyyy|yy|counter(:D\d+)?)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^counter:D(\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex CounterDigitsRegex();

    private enum PartKind
    {
        Literal,
        Year4,
        Year2,
        Counter
    }

    private sealed record Part(PartKind Kind, string? Text, int Digits);
}
