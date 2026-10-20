using System.Collections;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CrateCompat;

/// <summary>
///     AB#7149: what the INSERT path of the archives sends as bind parameters, per column type, against a real CrateDB.
///     CrateDB 6.4 checks the types of the VALUES of an INSERT more strictly than 5.x (<c>Cannot convert VALUES element in
///     row 1 of type X to Y</c>), and during the rolling upgrade both lines run side by side. Run with
///     <c>OCTO_TEST_CRATEDB_IMAGE=crate:5.10.10</c> and with the default <c>crate:6.4.6</c>: the cases below are accepted and
///     stored the same on both. The values are the CLR values the producers hand to <c>InsertAsync</c> (the pipeline nodes
///     <c>SaveStreamDataInArchive</c> / <c>SaveTimeRangeStreamDataInArchive</c> and the REST insert) and the ones the
///     format-1 import reads from NDJSON (<c>long</c>, <c>double</c>, <c>string</c>, <c>bool</c>, <c>DateTime</c>).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class CrateInsertTypeCompatibilityTests(CrateStagingCopyFixture fixture)
{
    private static readonly DateTime Utc = new(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);

    private const long UtcEpochMs = 1772600767123;

    private const string StrictRecord = "OBJECT(STRICT) AS (\"name\" TEXT, \"value\" DOUBLE PRECISION)";

    private const string StrictRecordArray = "ARRAY(OBJECT(STRICT) AS (\"name\" TEXT, \"value\" DOUBLE PRECISION))";

    /// <summary>Column type, label, the CLR value that is bound, and how the cell reads back.</summary>
    private static readonly (string Type, string Label, object Value, string Stored)[] Accepted =
    [
        // Numbers. The pipelines hand over int / long / double; the import hands over long / double / string.
        ("INTEGER", "int", 5, "5"),
        ("INTEGER", "long", 5L, "5"),
        ("INTEGER", "short", (short)5, "5"),
        ("INTEGER", "double without fraction", 5.0, "5"),
        ("INTEGER", "decimal", 5m, "5"),
        ("INTEGER", "numeric string", "5", "5"),
        ("BIGINT", "int", 5, "5"),
        ("BIGINT", "long", 5_000_000_000L, "5000000000"),
        ("BIGINT", "double without fraction", 5.0, "5"),
        ("BIGINT", "numeric string", "5", "5"),
        ("DOUBLE PRECISION", "int", 5, "5"),
        ("DOUBLE PRECISION", "long", 5L, "5"),
        ("DOUBLE PRECISION", "double", 5.5, "5.5"),
        ("DOUBLE PRECISION", "float", 5.5f, "5.5"),
        ("DOUBLE PRECISION", "decimal", 5.5m, "5.5"),
        ("DOUBLE PRECISION", "numeric string", "5.5", "5.5"),

        // Text and boolean.
        ("TEXT", "string", "abc", "abc"),
        ("TEXT", "int", 5, "5"),
        ("TEXT", "double", 5.5, "5.5"),
        ("TEXT", "char", 'x', "x"),
        ("BOOLEAN", "bool", true, "True"),
        ("BOOLEAN", "string", "true", "True"),
        ("BOOLEAN", "int", 1, "True"),

        // Time stamps: UTC, local and unspecified DateTime, the zero offset, ISO strings from the import, epoch milliseconds.
        ("TIMESTAMP WITH TIME ZONE", "DateTime utc", Utc, "1772600767123"),
        ("TIMESTAMP WITH TIME ZONE", "DateTime unspecified", DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified), "1772600767123"),
        ("TIMESTAMP WITH TIME ZONE", "DateTime local", DateTime.SpecifyKind(Utc, DateTimeKind.Local), "1772600767123"),
        ("TIMESTAMP WITH TIME ZONE", "DateTimeOffset with offset 0", new DateTimeOffset(Utc), "1772600767123"),
        ("TIMESTAMP WITH TIME ZONE", "ISO string", "2026-03-04T05:06:07.123Z", "1772600767123"),
        ("TIMESTAMP WITH TIME ZONE", "ISO string without zone", "2026-03-04T05:06:07.123", "1772600767123"),
        ("TIMESTAMP WITH TIME ZONE", "epoch milliseconds", UtcEpochMs, "1772600767123"),

        // Geo point and arrays of scalars.
        ("GEO_POINT", "double[]", new[] { 16.37, 48.2 }, "16.369999973103404,48.19999999366701"),
        ("GEO_POINT", "List<double>", new List<double> { 16.37, 48.2 }, "16.369999973103404,48.19999999366701"),
        ("GEO_POINT", "WKT string", "POINT (16.37 48.2)", "16.369999973103404,48.19999999366701"),
        ("ARRAY(TEXT)", "string[]", new[] { "a", "b" }, "[a,b]"),
        ("ARRAY(TEXT)", "List<string>", new List<string> { "a", "b" }, "[a,b]"),
        ("ARRAY(TEXT)", "empty string[]", Array.Empty<string>(), "[]"),
        ("ARRAY(INTEGER)", "int[]", new[] { 1, 2 }, "1,2"),
        ("ARRAY(INTEGER)", "List<int>", new List<int> { 1, 2 }, "1,2"),
        ("ARRAY(INTEGER)", "long[]", new[] { 1L, 2L }, "1,2"),
        ("ARRAY(DOUBLE PRECISION)", "double[]", new[] { 1.5, 2.5 }, "1.5,2.5"),
        ("ARRAY(DOUBLE PRECISION)", "int[]", new[] { 1, 2 }, "1.0,2.0"),

        // Records: the JSON text of the object, and an array of records as an array of the JSON texts of its elements.
        (StrictRecord, "JSON text", "{\"name\":\"n\",\"value\":1.5}", "{\"name\":\"n\",\"value\":1.5}"),
        (StrictRecord, "JSON text with an integer for the double", "{\"name\":\"n\",\"value\":2}", "{\"name\":\"n\",\"value\":2.0}"),
        (StrictRecordArray, "string[] of JSON texts", new[] { "{\"name\":\"n\",\"value\":1.5}" }, "[{\"name\":\"n\",\"value\":1.5}]"),
    ];

    /// <summary>
    ///     Values that CrateDB refuses on both lines. The point of the test is that a refusal stays a refusal: it must surface
    ///     as an error of the statement, never as a silently wrong cell.
    /// </summary>
    private static readonly (string Type, string Label, object Value)[] RefusedOnBothLines =
    [
        ("INTEGER", "text that is not a number", "abc"),
        ("INTEGER", "fractional text", "5.5"),
        ("INTEGER", "long out of the range", 3_000_000_000L),
        ("INTEGER", "bool", true),
        ("BIGINT", "TimeSpan", TimeSpan.FromHours(1)),
        ("DOUBLE PRECISION", "bool", true),
        ("BOOLEAN", "text that is not a bool", "x"),
        ("TEXT", "Guid", Guid.Parse("11111111-2222-3333-4444-555555555555")),
        ("OBJECT(STRICT) AS (\"name\" TEXT, \"value\" DOUBLE PRECISION)", "undeclared key", "{\"name\":\"n\",\"value\":1.5,\"x\":1}"),
        ("ARRAY(TEXT)", "JSON text of an array", "[\"a\",\"b\"]"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RawInsert_EveryAcceptedCase_IsStoredAsExpected()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var failures = new List<string>();
        foreach (var group in Accepted.GroupBy(c => c.Type))
        {
            var table = await CreateTableAsync(tenantId, group.Key, windowed: false);
            foreach (var c in group)
            {
                var rtId = OctoObjectId.GenerateNewId();
                var point = new DataPointDto(new Dictionary<string, object?> { ["v"] = c.Value })
                {
                    RtId = rtId, CkTypeId = SensorType, Timestamp = Utc
                };
                await InsertAndCheckAsync(failures, group.Key, c.Label, c.Stored, table, rtId,
                    () => fixture.Client.InsertDataAsync(tenantId, table, ["v"], [point]));
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task WindowedInsert_EveryAcceptedCase_IsStoredAsExpected()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var failures = new List<string>();
        foreach (var group in Accepted.GroupBy(c => c.Type))
        {
            var table = await CreateTableAsync(tenantId, group.Key, windowed: true);
            foreach (var c in group)
            {
                var rtId = OctoObjectId.GenerateNewId();
                var point = new TimeRangeDataPointDto(new Dictionary<string, object?> { ["v"] = c.Value })
                {
                    RtId = rtId, CkTypeId = SensorType, From = Utc, To = Utc.AddHours(1)
                };
                await InsertAndCheckAsync(failures, group.Key, c.Label, c.Stored, table, rtId,
                    () => fixture.Client.InsertTimeRangeDataAsync(tenantId, table, ["v"], [point]));
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Insert_ARefusedCase_FailsTheStatementAndStoresNothing()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var unexpected = new List<string>();
        foreach (var group in RefusedOnBothLines.GroupBy(c => c.Type))
        {
            var table = await CreateTableAsync(tenantId, group.Key, windowed: false);
            foreach (var c in group)
            {
                var rtId = OctoObjectId.GenerateNewId();
                var point = new DataPointDto(new Dictionary<string, object?> { ["v"] = c.Value })
                {
                    RtId = rtId, CkTypeId = SensorType, Timestamp = Utc
                };
                var error = await Record.ExceptionAsync(() => fixture.Client.InsertDataAsync(tenantId, table, ["v"], [point]));
                if (error is null)
                {
                    unexpected.Add($"{group.Key} / {c.Label}: accepted, expected an error");
                    continue;
                }

                await fixture.SqlAsync($"REFRESH TABLE {table}");
                var stored = await fixture.CountAsync($"SELECT COUNT(*) FROM {table} WHERE \"rtid\" = '{rtId}'");
                if (stored != 0)
                {
                    unexpected.Add($"{group.Key} / {c.Label}: failed but {stored} row(s) were stored");
                }
            }
        }

        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected));
    }

    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));

    private async Task InsertAndCheckAsync(
        List<string> failures, string type, string label, string expected, string table, OctoObjectId rtId, Func<Task> insert)
    {
        try
        {
            await insert();
        }
        catch (Exception ex)
        {
            failures.Add($"{type} / {label}: rejected on CrateDB {CrateStagingCopyFixture.StorageVersion}: {Describe(ex)}");
            return;
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var actual = await ReadBackAsync(type, table, rtId);
        if (actual != expected)
        {
            failures.Add($"{type} / {label}: stored '{actual}', expected '{expected}'");
        }
    }

    private async Task<string> CreateTableAsync(string tenantId, string type, bool windowed)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());
        var columns = new[] { new ArchiveColumnDdl("v", ParseType(type), false, true) };
        await fixture.SqlAsync(windowed
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(table, columns, 1, 0, false)
            : ArchiveDdlGenerator.GenerateCreateTable(table, columns, 1, 0));
        return table;
    }

    /// <summary>
    ///     The cell as text. Timestamps as epoch milliseconds, a geo point as longitude,latitude, an array of scalars through
    ///     <c>array_to_string</c> (CrateDB cannot cast an array to text), everything else as the driver renders it.
    /// </summary>
    private async Task<string> ReadBackAsync(string type, string table, OctoObjectId rtId)
    {
        var expression = type switch
        {
            "GEO_POINT" => "CAST(longitude(\"v\") AS TEXT) || ',' || CAST(latitude(\"v\") AS TEXT)",
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => "CAST(\"v\" AS BIGINT)",
            "ARRAY(INTEGER)" or "ARRAY(DOUBLE PRECISION)" => "array_to_string(\"v\", ',')",
            _ => "\"v\"",
        };
        var rows = await fixture.QueryAsync($"SELECT {expression} AS v FROM {table} WHERE \"rtid\" = '{rtId}'");
        return rows.Count == 0 ? "<no row>" : Render(rows[0]["v"]);
    }

    private static string Render(object? value) => value switch
    {
        null => "<null>",
        string text => text,
        IDictionary dictionary => "{" + string.Join(",", dictionary.Keys.Cast<object>().Select(k => $"\"{k}\":{RenderMember(dictionary[k])}")) + "}",
        IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(Render)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>",
    };

    private static string RenderMember(object? value) => value is string text ? $"\"{text}\"" : Render(value);

    private static string Describe(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException!)
        {
            if (inner is PostgresException pg)
            {
                return $"{pg.SqlState}: {pg.MessageText}";
            }

            if (inner.InnerException is null)
            {
                break;
            }
        }

        return ex.Message;
    }

    private static CrateColumnType ParseType(string type) => type switch
    {
        "ARRAY(TEXT)" => new CrateColumnType.Array(new CrateColumnType.Primitive("TEXT")),
        "ARRAY(INTEGER)" => new CrateColumnType.Array(new CrateColumnType.Primitive("INTEGER")),
        "ARRAY(DOUBLE PRECISION)" => new CrateColumnType.Array(new CrateColumnType.Primitive("DOUBLE PRECISION")),
        StrictRecord => RecordType(),
        StrictRecordArray => new CrateColumnType.Array(RecordType()),
        _ => new CrateColumnType.Primitive(type),
    };

    private static CrateColumnType.StrictObject RecordType() => new(
        [new RecordField("name", new CrateColumnType.Primitive("TEXT")), new RecordField("value", new CrateColumnType.Primitive("DOUBLE PRECISION"))]);
}
