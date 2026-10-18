using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// One statement the staging copy sends to CrateDB, in two spellings: the text that is executed
/// (<see cref="Reveal"/>, for a <c>COPY</c> it carries the storage credential) and the text that may be
/// shown (<see cref="Redacted"/>, <see cref="ToString"/>). Nothing outside the code that executes the
/// statement calls <see cref="Reveal"/>; logs, exceptions and traces get <see cref="Redacted"/>, and a
/// text that may echo the statement — a driver error message — goes through <see cref="Scrub"/>.
/// </summary>
/// <remarks>
/// AB#5967, concept <c>concept-tenant-backup-native-archive-dump.md</c> §5. The credential rules live in
/// <see cref="ArchiveCopyRedaction"/> and <see cref="ArchiveCopyStagingLocation"/> (engine contracts); this
/// type only keeps the two spellings together so no caller has to remember which string is which.
/// </remarks>
internal sealed class CopyStatement
{
    private readonly string _sql;
    private readonly Func<string, string> _scrub;

    private CopyStatement(string sql, string redacted, bool returnsRows, Func<string, string> scrub)
    {
        _sql = sql;
        Redacted = redacted;
        ReturnsRows = returnsRows;
        _scrub = scrub;
    }

    /// <summary>The statement as it may be shown: no key, secret or SAS token.</summary>
    public string Redacted { get; }

    /// <summary>True when the statement returns a result set (<c>RETURN SUMMARY</c>, a query).</summary>
    public bool ReturnsRows { get; }

    /// <summary>True for a <c>COPY</c> statement.</summary>
    public bool IsCopy => IsCopyStatement(_sql);

    /// <summary>The statement as it is executed. <b>May contain the credential</b>; never log it.</summary>
    public string Reveal() => _sql;

    /// <summary>Removes the credential from a text that may echo the statement.</summary>
    public string Scrub(string text) => _scrub(text);

    /// <summary>The redacted text.</summary>
    public override string ToString() => Redacted;

    /// <summary>A statement without a credential (metadata query, <c>ALTER TABLE</c>, <c>OPTIMIZE</c>).</summary>
    public static CopyStatement Plain(string sql, bool returnsRows = false)
        => new(sql, sql, returnsRows, static text => text);

    // ---- COPY statements --------------------------------------------------------------------------------

    private static readonly Regex OptionName = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Options whose value is a boolean or a number and is written without quotes.
    private static readonly HashSet<string> BareValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "overwrite_duplicates", "shared", "wait_for_completion", "fail_fast", "empty_string_as_null",
        "num_readers", "bulk_size"
    };

    // Options the statement decides itself; a location must not set them.
    private static readonly HashSet<string> StatementOwnedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "compression", "format", "overwrite_duplicates"
    };

    /// <summary>
    /// <c>COPY &lt;table&gt; WHERE &lt;time&gt; ∈ [from, to) TO DIRECTORY '&lt;location&gt;' WITH (compression = 'gzip', …)</c>
    /// — one time slice of a data table as gzip-compressed, line-wise JSON.
    /// </summary>
    public static CopyStatement CopyTo(
        string qualifiedTable, string timeColumn, DateTime fromUtc, DateTime toUtc,
        ArchiveCopyStagingLocation location)
    {
        string Render(bool redacted)
        {
            var sb = new StringBuilder();
            sb.Append("COPY ").Append(qualifiedTable)
                .Append(" WHERE \"").Append(timeColumn).Append("\" >= ").Append(Literal(FormatTs(fromUtc)))
                .Append(" AND \"").Append(timeColumn).Append("\" < ").Append(Literal(FormatTs(toUtc)))
                .Append(" TO DIRECTORY ").Append(Literal(redacted ? location.RedactedUri : location.RevealUri()))
                .Append(" WITH (").Append(Options(location, redacted, overwriteDuplicates: null)).Append(')');
            return sb.ToString();
        }

        return new CopyStatement(Render(false), Render(true), returnsRows: false, location.Scrub);
    }

    /// <summary>
    /// <c>COPY &lt;table&gt; FROM '&lt;location&gt;/*' WITH (compression = 'gzip', …) RETURN SUMMARY</c> — loads
    /// every file of a slice folder. The summary has one row per file and node.
    /// </summary>
    public static CopyStatement CopyFrom(
        string qualifiedTable, ArchiveCopyStagingLocation sliceLocation, bool overwriteDuplicates)
    {
        string Render(bool redacted)
        {
            var uri = (redacted ? sliceLocation.RedactedUri : sliceLocation.RevealUri()).TrimEnd('/') + "/*";
            var sb = new StringBuilder();
            sb.Append("COPY ").Append(qualifiedTable)
                .Append(" FROM ").Append(Literal(uri))
                .Append(" WITH (").Append(Options(sliceLocation, redacted, overwriteDuplicates)).Append(')')
                .Append(" RETURN SUMMARY");
            return sb.ToString();
        }

        return new CopyStatement(Render(false), Render(true), returnsRows: true, sliceLocation.Scrub);
    }

    private static string Options(ArchiveCopyStagingLocation location, bool redacted, bool? overwriteDuplicates)
    {
        var parts = new List<string> { "compression = 'gzip'" };
        if (overwriteDuplicates is { } overwrite)
        {
            parts.Add("overwrite_duplicates = " + (overwrite ? "true" : "false"));
        }

        var options = redacted ? location.RedactedOptions : location.RevealOptions();
        foreach (var (name, value) in options.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = name.ToLowerInvariant();
            if (!OptionName.IsMatch(key))
            {
                // Option names become identifiers of the statement; the value is never part of this message.
                throw new ArgumentException($"'{name}' is not a usable COPY option name.", nameof(location));
            }

            if (StatementOwnedOptions.Contains(key))
            {
                throw new ArgumentException($"The COPY option '{name}' is set by the statement, not by the location.",
                    nameof(location));
            }

            parts.Add(key + " = " + OptionValue(key, value));
        }

        return string.Join(", ", parts);
    }

    private static string OptionValue(string key, string value)
    {
        if (!BareValueOptions.Contains(key))
        {
            return Literal(value);
        }

        if (bool.TryParse(value, out var boolean))
        {
            return boolean ? "true" : "false";
        }

        // A masked value ("***") of a bare option stays a string so the redacted text is still readable.
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? value : Literal(value);
    }

    /// <summary>A single-quoted SQL string literal.</summary>
    internal static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string FormatTs(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    // ---- recognising a COPY ------------------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="sql"/> is a <c>COPY</c> statement: its first word, behind whitespace and
    /// SQL comments, is <c>COPY</c>. Decides which commands the tracing leaves out and which statements
    /// are reported without their text.
    /// </summary>
    public static bool IsCopyStatement(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return false;
        }

        var i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        const string keyword = "COPY";
        if (sql.Length - i < keyword.Length ||
            string.Compare(sql, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        return sql.Length == i + keyword.Length || !(char.IsLetterOrDigit(sql[i + keyword.Length]) || sql[i + keyword.Length] == '_');
    }
}
