using System;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Validates the ad-hoc LOCF statement for TimeWeightedAvg over raw event archives
/// (concept-time-weighted §6.2, AB#4336): carry-in sub-select, LEAD interval weighting,
/// carry-guarded plain aggregates, grouped and ungrouped shapes, and scope predicates on
/// both scans.
/// </summary>
public class TimeWeightedQuerySqlBuilderTests
{
    private const string SourceTable = "\"acmecorp\".\"archive_source\"";
    private const string CkTypeId = "EnergyIQ/Luminaire";

    private static readonly DateTime From = new(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 7, 4, 14, 0, 0, DateTimeKind.Utc);

    private static readonly TimeWeightedQuerySqlBuilder.TwaColumn Twa =
        new("dimminglevel", "dimminglevel_twavg");

    [Fact]
    public void Build_SingleTwaColumn_EmitsLocfShape()
    {
        var sql = TimeWeightedQuerySqlBuilder.Build(
            SourceTable, CkTypeId, From, To,
            new[] { Twa },
            Array.Empty<TimeWeightedQuerySqlBuilder.PlainColumn>(),
            Array.Empty<string>(),
            rtIds: null,
            carryLookback: TimeSpan.FromDays(35));

        // Ratio computed directly — nothing is materialised at query time.
        Assert.Contains(
            "SUM(CASE WHEN \"dimminglevel\" IS NOT NULL THEN \"dimminglevel\" * \"dt_ms\" END)"
            + " / NULLIF(SUM(CASE WHEN \"dimminglevel\" IS NOT NULL THEN \"dt_ms\" END), 0) AS \"dimminglevel_twavg\"",
            sql);
        // Carry-in row per rtId, bounded lookback: 35 days before 2026-07-04 10:00 = 2026-05-30.
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"timestamp\" DESC) AS \"rn\"", sql);
        Assert.Contains("'2026-05-30T10:00:00.0000000Z'::timestamp", sql);
        Assert.Contains("TRUE AS \"is_carry\"", sql);
        // Interval weighting capped at the window end, carry first on ties.
        Assert.Contains("LEAD(\"ts\") OVER (PARTITION BY \"rtid\" ORDER BY \"ts\", \"is_carry\" DESC)", sql);
        Assert.Contains("'2026-07-04T14:00:00.0000000Z'::timestamp)::bigint - \"ts\"::bigint AS \"dt_ms\"", sql);
        // Ungrouped: single result row, no GROUP BY.
        Assert.DoesNotContain("GROUP BY", sql);
        // ckTypeId predicate on the scans.
        Assert.Contains("AND \"cktypeid\" = 'EnergyIQ/Luminaire'", sql);
    }

    [Fact]
    public void Build_Grouped_WithPlainColumn_GuardsCarryAndGroups()
    {
        var sql = TimeWeightedQuerySqlBuilder.Build(
            SourceTable, CkTypeId, From, To,
            new[] { Twa },
            new[] { new TimeWeightedQuerySqlBuilder.PlainColumn("MAX", "dimminglevel", "dimminglevel_max") },
            new[] { "rtwellknownname" },
            rtIds: null,
            carryLookback: TimeSpan.FromDays(35));

        // Plain aggregates never see the carry-in virtual row.
        Assert.Contains("MAX(CASE WHEN NOT \"is_carry\" THEN \"dimminglevel\" END) AS \"dimminglevel_max\"", sql);
        // Group column selected verbatim and grouped in the outer SELECT.
        Assert.Contains("SELECT \"rtwellknownname\",", sql);
        Assert.Contains("GROUP BY \"rtwellknownname\"", sql);
    }

    [Fact]
    public void Build_RtIdScope_AppliesToCarryAndWindowScans()
    {
        var sql = TimeWeightedQuerySqlBuilder.Build(
            SourceTable, CkTypeId, From, To,
            new[] { Twa },
            Array.Empty<TimeWeightedQuerySqlBuilder.PlainColumn>(),
            Array.Empty<string>(),
            rtIds: new[] { "mp-1", "mp-2" },
            carryLookback: TimeSpan.FromDays(35));

        var occurrences = sql.Split("AND \"rtid\" IN ('mp-1', 'mp-2')").Length - 1;
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void Build_InvalidWindow_Throws()
    {
        Assert.Throws<ArgumentException>(() => TimeWeightedQuerySqlBuilder.Build(
            SourceTable, CkTypeId, To, From,
            new[] { Twa },
            Array.Empty<TimeWeightedQuerySqlBuilder.PlainColumn>(),
            Array.Empty<string>(), null, TimeSpan.FromDays(35)));
    }

    [Fact]
    public void Build_FieldFilters_ApplyToCarryAndWindowScans()
    {
        var filters = new[]
        {
            new Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.StreamDataFieldFilterDto(
                "ison", Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.StreamDataFieldFilterOperator.Equals,
                "true", null, null),
        };

        var sql = TimeWeightedQuerySqlBuilder.Build(
            SourceTable, CkTypeId, From, To,
            new[] { Twa },
            Array.Empty<TimeWeightedQuerySqlBuilder.PlainColumn>(),
            Array.Empty<string>(),
            rtIds: null,
            carryLookback: TimeSpan.FromDays(35),
            fieldFilters: filters);

        // The filter selects the event set — both the carry scan and the in-window scan.
        var occurrences = sql.Split("AND \"ison\" = 'true'").Length - 1;
        Assert.Equal(2, occurrences);
    }
}
