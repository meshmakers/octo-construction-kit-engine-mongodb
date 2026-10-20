using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CrateCompat;

/// <summary>
///     AB#7149: the places that decide on the text or the SQL state of a CrateDB error, against the errors a real CrateDB
///     answers with. The classifiers match on message text, so a CrateDB version that rewords an error would silently turn
///     "no table yet" into a failure (or the other way round). CrateDB 6.2 renumbered its HTTP error codes, which the driver
///     does not use, but 6.x also changed SQL states (AB#7138); this pins what every line of CrateDB answers. Run with
///     <c>OCTO_TEST_CRATEDB_IMAGE=crate:5.10.10</c> and with the default <c>crate:6.4.6</c>.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class CrateErrorContractTests(CrateStagingCopyFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> CreateTableAsync(string tenantId)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(
            table, [new ArchiveColumnDdl("v", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true)], 1, 0));
        return table;
    }

    [Fact]
    public async Task AMissingTableInAnExistingSchema_IsClassifiedAsAMissingTable()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        await CreateTableAsync(tenantId);
        var missing = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Client.GetCountAsync(tenantId, $"SELECT COUNT(*) FROM {missing}", Ct));

        Assert.True(CrateDbStreamDataRepository.IsMissingTableError(error), Describe(error));
        Assert.True(CrateDbStreamDataRepository.IsRelationUnknown(error), Describe(error));
    }

    [Fact]
    public async Task ATenantWithoutASchema_IsClassifiedAsAMissingTable()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var missing = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Client.GetCountAsync(tenantId, $"SELECT COUNT(*) FROM {missing}", Ct));

        Assert.True(CrateDbStreamDataRepository.IsMissingTableError(error), Describe(error));
    }

    [Fact]
    public async Task AnUnknownColumn_IsNotClassifiedAsAMissingTable()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var table = await CreateTableAsync(tenantId);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Client.GetCountAsync(tenantId, $"SELECT COUNT(\"nosuchcolumn\") FROM {table}", Ct));

        Assert.False(CrateDbStreamDataRepository.IsMissingTableError(error), Describe(error));
    }

    [Fact]
    public async Task ASyntaxError_IsNotClassifiedAsAMissingTable()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        await CreateTableAsync(tenantId);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Client.GetCountAsync(tenantId, "SELECT FROM FROM", Ct));

        Assert.False(CrateDbStreamDataRepository.IsMissingTableError(error), Describe(error));
        Assert.False(CrateDbStreamDataRepository.IsColumnAlreadyExists(error), Describe(error));
    }

    [Fact]
    public async Task AddingAColumnThatExists_IsClassifiedAsColumnAlreadyExists()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var table = await CreateTableAsync(tenantId);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Client.ExecuteDdlAsync(tenantId, $"ALTER TABLE {table} ADD COLUMN \"v\" DOUBLE PRECISION", Ct));

        Assert.True(CrateDbStreamDataRepository.IsColumnAlreadyExists(error), Describe(error));
    }

    [Fact]
    public async Task AFailedStatementReportsItsOwnError_NotTheResetRollback()
    {
        // The error of the statement must come out as it is. On 6.x the reset ROLLBACK of the pooled connection is a syntax error
        // (42601) instead of XX000, which is what AB#7138 fixed; the missing table (42P01) is what the statement itself answers.
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        await CreateTableAsync(tenantId);
        var missing = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Client.GetCountAsync(tenantId, $"SELECT COUNT(*) FROM {missing}", Ct));

        var postgres = Find<PostgresException>(error);
        Assert.NotNull(postgres);
        Assert.DoesNotContain("ROLLBACK", postgres.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("42P01", postgres.SqlState);
    }

    [Fact]
    public async Task TheCatalogReadsOfTheClient_AnswerWithTheirColumns()
    {
        // sys.shards, sys.health and information_schema as the client reads them (table statistics, table listing).
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var table = await CreateTableAsync(tenantId);
        var name = table[(table.LastIndexOf('.') + 1)..].Trim('"');
        await fixture.Client.InsertDataAsync(tenantId, table, ["v"],
        [
            new DataPointDto(new Dictionary<string, object?> { ["v"] = 1.5 })
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")),
                Timestamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            },
        ]);
        await fixture.SqlAsync($"REFRESH TABLE {table}");

        var tables = await fixture.Client.ListTablesInTenantSchemaAsync(tenantId);
        var stats = await fixture.Client.GetTableStatsAsync(tenantId, [name], Ct);

        Assert.Contains(name, tables);
        var row = Assert.Single(stats);
        Assert.Equal(name, row.TableName);
        Assert.Equal(1, row.NumDocs);
        Assert.NotNull(row.Health);
    }

    private static T? Find<T>(Exception error) where T : Exception
    {
        for (var current = error; current is not null; current = current.InnerException!)
        {
            if (current is T match)
            {
                return match;
            }

            if (current.InnerException is null)
            {
                break;
            }
        }

        return null;
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";
}
