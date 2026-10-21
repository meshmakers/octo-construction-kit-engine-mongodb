using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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

    /// <summary>
    ///     AB#7256: the asset repository caches a file and a background job reads it in the same second. The download
    ///     must find the file right after the write completed (read-your-writes), also on a replica set whose
    ///     secondaries may lag behind.
    /// </summary>
    [Fact]
    public async Task Cache_UploadThenImmediateDownload_FindsTheFile()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var content = "{\"ab7256\":true}"u8.ToArray();

        OctoObjectId id;
        using (var uploadSession = await tenantRepository.GetSessionAsync())
        {
            uploadSession.StartTransaction();
            id = await tenantRepository.UploadTemporaryLargeBinaryAsync(uploadSession, "ab7256.json",
                "application/json", DateTime.UtcNow.AddHours(1), new MemoryStream(content), CancellationToken.None);
            await uploadSession.CommitTransactionAsync();
        }

        // A separate session, as the consuming job has its own.
        using var downloadSession = await tenantRepository.GetSessionAsync();
        downloadSession.StartTransaction();
        var handler = await tenantRepository.DownloadLargeBinaryAsync(downloadSession, id, CancellationToken.None);
        await using var target = new MemoryStream();
        await handler.Stream.CopyToAsync(target, TestContext.Current.CancellationToken);

        Assert.Equal(content, target.ToArray());
    }

    [Fact]
    public async Task Cache_DownloadOfUnknownId_ThrowsEntityNotFound()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        await Assert.ThrowsAsync<EntityNotFoundException>(() => tenantRepository.DownloadLargeBinaryAsync(session,
            OctoObjectId.GenerateNewId(), CancellationToken.None));
    }
}
