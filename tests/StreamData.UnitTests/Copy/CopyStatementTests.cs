using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#5967 (slice AB#6967): the text of a COPY statement carries the storage credential. The executed text
/// and the text that may be shown are built together, and no spelling of the shown one holds a canary.
/// </summary>
public class CopyStatementTests
{
    private const string Table = "\"tenant1\".\"archive_65d5c447b420da3fb12381bc\"";
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    // The secret contains '/' and '+' on purpose, as real S3 secret keys do.
    private const string CanaryKeyId = "CANARYKEYID1234";
    private const string CanarySecret = "CANARY/SECRET+value==";
    private const string CanarySas = "sv=CANARYSAS&sig=CANARYSIG";
    private const string CanaryAccountKey = "CANARYACCOUNTKEY==";

    private static readonly string[] AllCanaries =
        [CanaryKeyId, CanarySecret, "CANARY/SECRET", "SECRET+value", CanarySas, "CANARYSIG", CanaryAccountKey];

    private static ArchiveCopyStagingLocation S3() =>
        new($"s3://{CanaryKeyId}:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1/65d5c447b420da3fb12381bc/data/s1",
            new Dictionary<string, string> { ["protocol"] = "http" });

    private static ArchiveCopyStagingLocation Az() =>
        new("az://acct.blob.core.windows.net/octo-artifacts/copy-staging/t1/run1/65d5c447b420da3fb12381bc/data/s1",
            new Dictionary<string, string> { ["sas_token"] = CanarySas, ["key"] = CanaryAccountKey });

    private static void AssertNoCanary(string text)
    {
        foreach (var canary in AllCanaries)
        {
            Assert.False(text.Contains(canary, StringComparison.Ordinal), $"'{canary}' is in: {text}");
        }
    }

    [Fact]
    public void CopyTo_S3_PutsTheKeyInTheUriAndTheProtocolInWith_AndRedactedTextKeepsTheRest()
    {
        var statement = CopyStatement.CopyTo(Table, "timestamp", From, To, S3());

        var sql = statement.Reveal();
        Assert.StartsWith($"COPY {Table} WHERE \"timestamp\" >= '2026-01-01 00:00:00.000' AND \"timestamp\" < '2026-01-02 03:04:05.006' TO DIRECTORY 's3://", sql);
        Assert.Contains($"{CanaryKeyId}:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/", sql);
        Assert.EndsWith("WITH (compression = 'gzip', protocol = 'http')", sql);
        Assert.False(statement.ReturnsRows);
        Assert.True(statement.IsCopy);

        AssertNoCanary(statement.Redacted);
        AssertNoCanary(statement.ToString());
        Assert.Contains("TO DIRECTORY 's3://***@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1/65d5c447b420da3fb12381bc/data/s1'", statement.Redacted);
        Assert.Contains("WITH (compression = 'gzip', protocol = 'http')", statement.Redacted);
    }

    [Fact]
    public void CopyTo_Az_PutsSasTokenAndKeyInWith_AndRedactedTextMasksBoth()
    {
        var statement = CopyStatement.CopyTo(Table, "window_start", From, To, Az());

        var sql = statement.Reveal();
        Assert.Contains("TO DIRECTORY 'az://acct.blob.core.windows.net/octo-artifacts/copy-staging/", sql);
        Assert.Contains($"key = '{CanaryAccountKey}'", sql);
        Assert.Contains($"sas_token = '{CanarySas}'", sql);

        AssertNoCanary(statement.Redacted);
        Assert.Contains("key = '***'", statement.Redacted);
        Assert.Contains("sas_token = '***'", statement.Redacted);
        Assert.Contains("az://acct.blob.core.windows.net/octo-artifacts/copy-staging/", statement.Redacted);
    }

    [Fact]
    public void CopyFrom_LoadsEveryFileOfTheSliceFolder_WithReturnSummary()
    {
        var statement = CopyStatement.CopyFrom(Table, S3(), overwriteDuplicates: false);

        Assert.EndsWith("/data/s1/*' WITH (compression = 'gzip', overwrite_duplicates = false, protocol = 'http') RETURN SUMMARY",
            statement.Reveal());
        Assert.StartsWith($"COPY {Table} FROM 's3://{CanaryKeyId}:", statement.Reveal());
        Assert.True(statement.ReturnsRows);
        AssertNoCanary(statement.Redacted);
        Assert.Contains("FROM 's3://***@minio.octo-infra:9000/", statement.Redacted);
        Assert.EndsWith("RETURN SUMMARY", statement.Redacted);
    }

    [Fact]
    public void CopyFrom_OverwriteDuplicates_IsAStatementOptionNotALocationOption()
    {
        Assert.Contains("overwrite_duplicates = true",
            CopyStatement.CopyFrom(Table, S3(), overwriteDuplicates: true).Reveal());

        var location = new ArchiveCopyStagingLocation("s3://h/b/p",
            new Dictionary<string, string> { ["overwrite_duplicates"] = "true" });
        Assert.Throws<ArgumentException>(() => CopyStatement.CopyFrom(Table, location, false));
        Assert.Throws<ArgumentException>(() => CopyStatement.CopyTo(Table, "timestamp", From, To,
            new ArchiveCopyStagingLocation("s3://h/b/p", new Dictionary<string, string> { ["compression"] = "none" })));
    }

    [Fact]
    public void CopyFrom_BulkSize_IsWrittenOnlyWhenSet_AndThenOwnedByTheStatement()
    {
        Assert.DoesNotContain("bulk_size", CopyStatement.CopyFrom(Table, S3(), overwriteDuplicates: false).Reveal());
        var statement = CopyStatement.CopyFrom(Table, S3(), overwriteDuplicates: true, bulkSize: 5000);
        Assert.Contains("overwrite_duplicates = true, bulk_size = 5000", statement.Reveal());
        Assert.Contains("bulk_size = 5000", statement.Redacted);
        AssertNoCanary(statement.Redacted);

        var location = new ArchiveCopyStagingLocation("s3://h/b/p", new Dictionary<string, string> { ["bulk_size"] = "100" });
        // a location may set it while the statement leaves it alone (today's behaviour) ...
        Assert.Contains("bulk_size = 100", CopyStatement.CopyFrom(Table, location, false).Reveal());
        // ... but not when the statement owns it
        Assert.Throws<ArgumentException>(() => CopyStatement.CopyFrom(Table, location, true, 5000));
    }

    [Fact]
    public void OptionNames_BecomeIdentifiers_SoOnlyPlainNamesAreAccepted_WithoutEchoingTheValue()
    {
        var location = new ArchiveCopyStagingLocation("s3://h/b/p",
            new Dictionary<string, string> { ["x) TO DIRECTORY 'evil'; --"] = "CANARYVALUE" });

        var ex = Assert.Throws<ArgumentException>(() => CopyStatement.CopyTo(Table, "timestamp", From, To, location));

        Assert.DoesNotContain("CANARYVALUE", ex.Message);
    }

    [Fact]
    public void QuotesInTheUriAndValues_AreEscaped()
    {
        var statement = CopyStatement.CopyTo(Table, "timestamp", From, To,
            new ArchiveCopyStagingLocation("s3://k:s@h/b'ucket/p", new Dictionary<string, string> { ["sas_token"] = "a'b" }));

        Assert.Contains("TO DIRECTORY 's3://k:s@h/b''ucket/p'", statement.Reveal());
        Assert.Contains("sas_token = 'a''b'", statement.Reveal());
    }

    [Fact]
    public void Scrub_RemovesTheCredentialFromADriverMessage()
    {
        var statement = CopyStatement.CopyTo(Table, "timestamp", From, To, S3());
        var echo = $"SQLParseException: Cannot access 's3://{CanaryKeyId}:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/x'";

        var scrubbed = statement.Scrub(echo);

        AssertNoCanary(scrubbed);
        Assert.Contains("minio.octo-infra:9000/octo-artifacts/x", scrubbed);
    }

    [Fact]
    public void PlainStatements_ShowTheirTextAsItIs()
    {
        var statement = CopyStatement.Plain("ALTER TABLE t SET (number_of_replicas = 0)");

        Assert.Equal("ALTER TABLE t SET (number_of_replicas = 0)", statement.Redacted);
        Assert.Equal(statement.Reveal(), statement.Redacted);
        Assert.False(statement.IsCopy);
    }

    [Theory]
    [InlineData("COPY t TO DIRECTORY 'x'", true)]
    [InlineData("copy t from 'x'", true)]
    [InlineData("  \n\tCOPY t FROM 'x'", true)]
    [InlineData("-- a comment\nCOPY t FROM 'x'", true)]
    [InlineData("/* c */ COPY t FROM 'x'", true)]
    [InlineData("COPY", true)]
    [InlineData("SELECT * FROM copy_jobs", false)]
    [InlineData("SELECT 'COPY'", false)]
    [InlineData("COPYRIGHT", false)]
    [InlineData("COPY_ALL", false)]
    [InlineData("INSERT INTO t VALUES (1) -- COPY", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCopyStatement_RecognisesTheFirstWord(string? sql, bool expected)
    {
        Assert.Equal(expected, CopyStatement.IsCopyStatement(sql));
    }
}
