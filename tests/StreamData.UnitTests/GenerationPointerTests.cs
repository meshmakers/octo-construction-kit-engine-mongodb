using System;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// AB#4184 Phase 6 — the per-window active-generation pointer (CrateDB side-table) and the read-path
// generation filter. Pins the SQL shapes that, together, give a partial-range recompute an atomic
// swap with no mixed reads.
public class GenerationPointerTests
{
    private const string Table = "\"meshtest\".\"archive_rollup1\"";

    [Fact]
    public void GenMapTable_IsArchiveTableWithGenMapSuffix_InTenantSchema()
    {
        var t = GenerationMapSqlBuilder.GenMapTable("acmecorp", "rollup1");

        Assert.Equal("\"acmecorp\".\"archive_rollup1__genmap\"", t);
    }

    [Fact]
    public void BuildCreateTable_KeysOnRangeTuple()
    {
        var sql = GenerationMapSqlBuilder.BuildCreateTable(GenerationMapSqlBuilder.GenMapTable("acmecorp", "rollup1"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS", sql);
        Assert.Contains("\"range_start\" BIGINT NOT NULL", sql);
        Assert.Contains("\"range_end\" BIGINT NOT NULL", sql);
        Assert.Contains("\"rtid_scope\" TEXT NOT NULL DEFAULT ''", sql);
        Assert.Contains("\"generation\" BIGINT NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (\"range_start\", \"range_end\", \"rtid_scope\")", sql);
    }

    [Fact]
    public void BuildNextGeneration_IsMaxPlusOne()
    {
        var sql = GenerationMapSqlBuilder.BuildNextGeneration("\"acmecorp\".\"archive_rollup1__genmap\"");

        Assert.Equal(
            "SELECT COALESCE(MAX(\"generation\"), 0) + 1 AS next FROM \"acmecorp\".\"archive_rollup1__genmap\";",
            sql);
    }

    [Fact]
    public void BuildUpsertPointer_UsesEpochMsBounds_AndUpsertsGeneration()
    {
        var genMap = GenerationMapSqlBuilder.GenMapTable("acmecorp", "rollup1");
        var from = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 11, 13, 0, 0, DateTimeKind.Utc);
        var fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to).ToUnixTimeMilliseconds();

        var sql = GenerationMapSqlBuilder.BuildUpsertPointer(genMap, from, to, GenerationMapSqlBuilder.AllRtIdsScope, 5);

        Assert.Contains($"VALUES ({fromMs}, {toMs}, '', 5)", sql);
        Assert.Contains("ON CONFLICT (\"range_start\", \"range_end\", \"rtid_scope\") DO UPDATE SET \"generation\" = 5", sql);
    }

    [Fact]
    public void BuildSelectAll_ProjectsRangeAndGeneration()
    {
        var sql = GenerationMapSqlBuilder.BuildSelectAll("\"acmecorp\".\"archive_rollup1__genmap\"");

        Assert.Equal(
            "SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM \"acmecorp\".\"archive_rollup1__genmap\";",
            sql);
    }

    [Fact]
    public void Compile_NotGenerationTracked_EmitsNoGenerationPredicate()
    {
        // A non-rollup (time-range) query never calls WithGenerationRanges, so no generation
        // predicate is emitted — those tables have no generation column.
        var qb = new CrateQueryBuilder(Table);
        qb.AddVariable("voltage_avg_sum", null, null);

        var sql = new CrateQueryCompiler().CompileQuery(qb);

        Assert.DoesNotContain("generation", sql);
        Assert.DoesNotContain("WHERE", sql);
    }

    [Fact]
    public void Compile_GenerationTracked_EmptyRanges_EmitsBaselineGenerationZero()
    {
        // Rollup read with an empty genmap (steady state, or mid-recompute before the pointer flip):
        // must still filter to generation 0 so not-yet-committed next-generation rows stay hidden.
        var qb = new CrateQueryBuilder(Table);
        qb.AddVariable("voltage_avg_sum", null, null);
        qb.WithGenerationRanges(System.Array.Empty<GenerationRange>());

        var sql = new CrateQueryCompiler().CompileQuery(qb);

        Assert.Contains("WHERE \"generation\" = 0", sql);
        Assert.DoesNotContain("CASE", sql);
    }

    [Fact]
    public void Compile_SingleGenerationRange_EmitsCaseElseZero()
    {
        var qb = new CrateQueryBuilder(Table);
        qb.AddVariable("voltage_avg_sum", null, null);
        qb.WithGenerationRanges(new[] { new GenerationRange(1000, 2000, "", 3) });

        var sql = new CrateQueryCompiler().CompileQuery(qb);

        Assert.Contains(
            "WHERE \"generation\" = CASE WHEN (\"window_start\" >= 1000 AND \"window_start\" < 2000) THEN 3 ELSE 0 END",
            sql);
    }

    [Fact]
    public void Compile_MultipleRanges_OrdersByGenerationDescending_SoNewerWins()
    {
        var qb = new CrateQueryBuilder(Table);
        qb.AddVariable("voltage_avg_sum", null, null);
        // Deliberately add the lower generation first; the compiler must emit the higher one first so
        // an overlapping re-recompute (higher generation) wins via CASE first-match.
        qb.WithGenerationRanges(new[]
        {
            new GenerationRange(1000, 2000, "", 2),
            new GenerationRange(1500, 2500, "", 5),
        });

        var sql = new CrateQueryCompiler().CompileQuery(qb);

        var idxGen5 = sql.IndexOf("THEN 5", StringComparison.Ordinal);
        var idxGen2 = sql.IndexOf("THEN 2", StringComparison.Ordinal);
        Assert.True(idxGen5 >= 0 && idxGen2 >= 0);
        Assert.True(idxGen5 < idxGen2, "Higher generation must be emitted first (CASE first-match).");
    }

    [Fact]
    public void Compile_GenerationRange_AndsWithTimeFilter()
    {
        var qb = new CrateQueryBuilder(Table);
        qb.AddVariable("voltage_avg_sum", null, null);
        qb.UseWindowedTimeAxis();
        qb.WithTimeFilter(
            new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc));
        qb.WithGenerationRanges(new[] { new GenerationRange(1000, 2000, "", 3) });

        var sql = new CrateQueryCompiler().CompileQuery(qb);

        // Time predicate AND generation predicate both present, connected by AND.
        Assert.Contains("\"window_start\" < '", sql);
        Assert.Contains("AND \"generation\" = CASE", sql);
    }

    [Fact]
    public void Compile_ScopedGenerationRange_AddsRtIdPredicate()
    {
        var qb = new CrateQueryBuilder(Table);
        qb.AddVariable("voltage_avg_sum", null, null);
        qb.WithGenerationRanges(new[] { new GenerationRange(1000, 2000, "mp-42", 4) });

        var sql = new CrateQueryCompiler().CompileQuery(qb);

        Assert.Contains("AND \"rtid\" = 'mp-42'", sql);
        Assert.Contains("THEN 4", sql);
    }
}
