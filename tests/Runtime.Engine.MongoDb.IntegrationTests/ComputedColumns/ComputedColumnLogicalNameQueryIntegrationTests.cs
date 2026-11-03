using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FakeItEasy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.ComputedColumns;

/// <summary>
///     AB#8236 against a real CrateDB: after a computed column's formula was changed once and twice
///     (physical column <c>doubled__v1</c> / <c>doubled__v2</c>) every query kind still returns the
///     logical name <c>doubled</c> (and <c>doubled_avg</c>, ...) with the values of the newest formula, and
///     a field filter on the column hits the versioned physical column.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ComputedColumnLogicalNameQueryIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 24 hourly rows with value = i * 1.5 (0 .. 34.5), mean 17.25, max 34.5.
    [Theory]
    [InlineData(0, 2.0)]
    [InlineData(1, 3.0)]
    [InlineData(2, 4.0)]
    public async Task EveryQueryKind_ReturnsTheLogicalName_WhateverTheFormulaVersion(int changes, double factor)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archive);
        // 2026-01-01 00:00 UTC, one row an hour; the type literal is what the CkTypeId filter of a query matches.
        var rawTable = TenantSchema.QualifiedArchiveTable(tenant, archive.ToString());
        await fixture.SqlAsync(
            $"INSERT INTO {rawTable} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
            "SELECT 'rt1', CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'Test/Sensor', generate_series * 1.5 " +
            "FROM generate_series(0, 23)");
        await fixture.SqlAsync($"REFRESH TABLE {rawTable}");

        var store = new MemoryArchiveStore();
        store.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "sensor",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false) }));
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = fixture.ConnectionString });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, A.Fake<IStreamDataDatabaseManagementClient>(),
            configuration, tenant, store, Formula);
        var lifecycle = new ArchiveLifecycleService(tenant, store, repository, A.Fake<IArchiveAuditTrail>(),
            NullLogger<ArchiveLifecycleService>.Instance, recomputeJobStore: new MemoryJobStore());

        await lifecycle.AddComputedColumnAsync(archive, "doubled", "value * 2", FormulaResultType.Double, indexed: false);
        for (var i = 1; i <= changes; i++)
        {
            await lifecycle.UpdateComputedColumnFormulaAsync(archive, "doubled", $"value * {i + 2}");
        }

        Assert.Equal(changes, store.Column(archive, "doubled")!.ComputedVersion);
        var table = TenantSchema.QualifiedArchiveTable(tenant, archive.ToString());
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var physical = changes == 0 ? "doubled" : $"doubled__v{changes}";
        Assert.Equal(24, await fixture.CountAsync($"SELECT count(*) FROM {table} WHERE \"{physical}\" IS NOT NULL"));

        // Aggregation (and the keys the GraphQL layer builds from the same logical name).
        var aggregation = await repository.ExecuteAggregationQueryAsync(archive,
            StreamDataAggregationQueryOptions.Create()
                .WithCkTypeId(SensorType)
                .WithAggregationColumns([
                    new AggregationColumn("doubled", AggregationFunction.Average),
                    new AggregationColumn("doubled", AggregationFunction.Maximum),
                    new AggregationColumn("doubled", AggregationFunction.Integral)])
                .WithTimeRange(From, To));
        var values = Assert.Single(aggregation.Rows).Values;
        Assert.Equal(["doubled_avg", "doubled_max", "doubled_integral"], values.Keys.Where(k => k.StartsWith("doubled_", StringComparison.Ordinal)).ToArray());
        Assert.DoesNotContain(values.Keys, k => k.Contains("__v", StringComparison.Ordinal));
        Assert.Equal(17.25 * factor, Convert.ToDouble(values["doubled_avg"]), 6);
        Assert.Equal(34.5 * factor, Convert.ToDouble(values["doubled_max"]), 6);

        // Simple query: column, sort and field filter on the computed column.
        var simple = await repository.ExecuteQueryAsync(archive,
            StreamDataQueryOptions.Create()
                .WithCkTypeId(SensorType)
                .WithColumns(["doubled"])
                .WithSortOrders([new SortOrderItem("doubled", SortOrders.Descending)])
                .WithFieldFilters([new FieldFilter("doubled", FieldFilterOperator.GreaterThan, (20 * factor).ToString(System.Globalization.CultureInfo.InvariantCulture))])
                .WithTimeRange(From, To));
        // value*factor > 20*factor  <=>  value > 20: rows 14..23 (value 21 .. 34.5) -> 10 rows, largest first.
        Assert.Equal(10, simple.Rows.Count);
        Assert.All(simple.Rows, r => Assert.Equal(["doubled"], r.Values.Keys.ToArray()));
        Assert.Equal(34.5 * factor, Convert.ToDouble(simple.Rows[0].Values["doubled"]), 6);

        // Grouped aggregation.
        var grouped = await repository.ExecuteGroupedAggregationQueryAsync(archive,
            StreamDataGroupedAggregationQueryOptions.Create()
                .WithCkTypeId(SensorType)
                .WithAggregationColumns([new AggregationColumn("doubled", AggregationFunction.Sum)])
                .WithGroupByColumns(["rtId"])
                .WithTimeRange(From, To));
        var groupedValues = Assert.Single(grouped.Rows).Values;
        Assert.Contains("doubled_sum", groupedValues.Keys);
        Assert.DoesNotContain(groupedValues.Keys, k => k.Contains("__v", StringComparison.Ordinal));
        Assert.Equal(414.0 * factor, Convert.ToDouble(groupedValues["doubled_sum"]), 6);

        // Downsampling.
        var downsampling = await repository.ExecuteDownsamplingQueryAsync(archive,
            StreamDataDownsamplingQueryOptions.Create()
                .WithCkTypeId(SensorType)
                .WithAggregationColumns([new AggregationColumn("doubled", AggregationFunction.Average)])
                .WithTimeRange(From, To)
                .WithLimit(4));
        Assert.NotEmpty(downsampling.Rows);
        Assert.All(downsampling.Rows, r =>
        {
            Assert.Contains("doubled_avg", r.Values.Keys);
            Assert.DoesNotContain(r.Values.Keys, k => k.Contains("__v", StringComparison.Ordinal));
        });
    }
}
