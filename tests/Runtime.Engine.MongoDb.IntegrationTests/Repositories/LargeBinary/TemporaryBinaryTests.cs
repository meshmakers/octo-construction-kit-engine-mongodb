using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.LargeBinary;

[Collection(ImportTestCkModelCollection.Name)]
public class LocalDirectoryRepositoryTemporaryBinaryTests(ImportTestCkModelFixture fixture)
{
    [Fact]
    public async Task Cache_UploadTemporaryLargeBinaryAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filePath = "testData/largeBinaries/Customers.xlsx";
        var stream = File.OpenRead(filePath);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var id = await tenantRepository.UploadTemporaryLargeBinaryAsync(session, "Customers.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", DateTime.UtcNow.AddHours(1), stream,
            CancellationToken.None);

        var r = await tenantRepository.GetTemporaryLargeBinaryAsync(session, id, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("Customers.xlsx", r.Filename);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.ContentType);
        Assert.Equal(5401, r.Size);
    }

    [Fact]
    public async Task Cache_DeleteTemporaryLargeBinaryAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filePath = "testData/largeBinaries/Customers.xlsx";
        var stream = File.OpenRead(filePath);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var id = await tenantRepository.UploadTemporaryLargeBinaryAsync(session, "Customers.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", DateTime.UtcNow.AddHours(1), stream,
            CancellationToken.None);

        await tenantRepository.DeleteTemporaryLargeBinaryAsync(session, id, CancellationToken.None);

        var r = await tenantRepository.GetTemporaryLargeBinaryAsync(session, id, CancellationToken.None);

        Assert.Null(r);
    }
}
