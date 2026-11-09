using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// The primary-key read of stored rows for the merged-row evaluation of computed columns (AB#8622).
/// </summary>
public class StoredRowLookupTests
{
    [Fact]
    public void RawSelect_OneEqualityTermPerKeyOnAllPrimaryKeyColumns()
    {
        var sql = StoredRowLookup.BuildSelectSql("\"t\".\"archive_x\"", windowed: false, ["production", "rtid"], 2);

        Assert.Equal(
            "SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"production\" FROM \"t\".\"archive_x\" WHERE " +
            "(\"timestamp\" = $1 AND \"rtid\" = $2 AND \"cktypeid\" = $3) OR " +
            "(\"timestamp\" = $4 AND \"rtid\" = $5 AND \"cktypeid\" = $6)",
            sql);
    }

    [Fact]
    public void WindowedSelect_KeysOnTheWholeWindow()
    {
        var sql = StoredRowLookup.BuildSelectSql("\"t\".\"archive_x\"", windowed: true, ["consumption"], 1);

        Assert.Equal(
            "SELECT \"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"consumption\" FROM \"t\".\"archive_x\" WHERE " +
            "(\"window_start\" = $1 AND \"window_end\" = $2 AND \"rtid\" = $3 AND \"cktypeid\" = $4)",
            sql);
    }

    [Fact]
    public void Key_OfAReturnedRow_MatchesTheSentKey_WhateverTheDateTimeKind()
    {
        var utc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var sent = new StoredRowKey("r1", "Test/Meter", utc, utc.AddMinutes(15));
        var returned = new Dictionary<string, object?>
        {
            ["window_start"] = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified),
            ["window_end"] = new DateTimeOffset(utc.AddMinutes(15)),
            ["rtid"] = "r1",
            ["cktypeid"] = "Test/Meter",
        };

        Assert.Equal(sent.Value, StoredRowKey.Of(returned, windowed: true));
    }

    [Fact]
    public void Key_OfARowWithoutItsKeyColumns_IsNull()
    {
        Assert.Null(StoredRowKey.Of(new Dictionary<string, object?> { ["rtid"] = "r1" }, windowed: false));
    }

    [Fact]
    public void AsUtc_ReadsAnUnspecifiedKindAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(DateTimeKind.Utc, StoredRowLookup.AsUtc(unspecified).Kind);
        Assert.Equal(unspecified.Ticks, StoredRowLookup.AsUtc(unspecified).Ticks);
    }
}
