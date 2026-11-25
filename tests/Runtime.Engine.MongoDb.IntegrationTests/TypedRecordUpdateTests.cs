using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     An update that carries a generated record class (<c>RtContactNameRecord</c>, a list of
///     <c>RtEMailAddressRecord</c>) as an attribute value is stored like the insert of the same entity: as a plain record.
///     Before, the boxed <c>$set</c> value was refused by the object serializer ("not configured as a type that is
///     allowed to be serialized"), so a typed record could be inserted but not updated.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class TypedRecordUpdateTests(ImportTestCkModelFixture fixture)
{
    private static readonly RtCkId<CkTypeId> CustomerType = new("Test/Customer");

    [Fact]
    public async Task ApplyChanges_UpdateWithTypedRecordAndTypedRecordList_IsStoredAndReadsBackTypedAndUntyped()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var customer = await repository.CreateTransientRtEntityAsync<RtCustomer>();
        customer.Name = new RtContactNameRecord { FirstName = "John", LastName = "Doe" };
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, customer);
            await session.CommitTransactionAsync();
        }

        var update = new RtEntity(CustomerType, customer.RtId, new Dictionary<string, object?>
        {
            ["Name"] = new RtContactNameRecord { FirstName = "Jane", LastName = "Roe" },
            ["EMailAddresses"] = new List<RtEMailAddressRecord>
            {
                new() { EMailAddress = "office@example.com", EMailAddressType = RtEMailAddressTypeEnum.Office }
            }
        });
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var result = new OperationResult();
            await repository.ApplyChangesAsync(session,
            [
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(CustomerType, customer.RtId), update)
            ], result);
            Assert.False(result.HasErrors || result.HasFatalErrors, result.GetMessages());
            await session.CommitTransactionAsync();
        }

        using var readSession = await repository.GetSessionAsync();
        readSession.StartTransaction();
        var typed = (await repository.GetRtEntitiesByIdAsync<RtCustomer>(readSession, [customer.RtId],
            RtEntityQueryOptions.Create())).Items.Single();
        var untyped = (await repository.GetRtEntitiesByIdAsync(readSession, CustomerType, [customer.RtId],
            RtEntityQueryOptions.Create())).Items.Single();
        await readSession.CommitTransactionAsync();

        Assert.Equal("Jane", typed.Name?.FirstName);
        Assert.Equal("Roe", typed.Name?.LastName);
        Assert.Equal("Jane", untyped.GetRtRecordAttributeValue<RtContactNameRecord>("Name")?.FirstName);
        var email = Assert.Single(untyped.GetRtRecordAttributeValues<RtEMailAddressRecord>("EMailAddresses"));
        Assert.Equal("office@example.com", email.EMailAddress);
        Assert.Equal(RtEMailAddressTypeEnum.Office, email.EMailAddressType);
    }
}
