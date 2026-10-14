using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Review L19: a full CkModel update must not write explicit nulls for the CK v2 members of a classic model
///     (<c>dependencyRanges</c>, <c>ckLanguage</c>) — they are unset, so the document keeps its pre-v2 shape.
/// </summary>
public class CkModelUpdateMapperTests
{
    private static BsonDocument Render(CkModel model)
    {
        var update = new CkModelMongoDataSourceMapper().ApplyUpdate(model);
        return update.Render(new RenderArgs<CkModel>(BsonSerializer.LookupSerializer<CkModel>(),
            BsonSerializer.SerializerRegistry)).AsBsonDocument;
    }

    private static IEnumerable<string> Keys(BsonDocument rendered, string op) =>
        rendered.TryGetValue(op, out var value) ? value.AsBsonDocument.Names : [];

    [Fact]
    public void ClassicModel_UnsetsCkV2Members_InsteadOfWritingNull()
    {
        var rendered = Render(new CkModel { Id = new CkModelId("Classic-1.0.0"), ModelId = "Classic" });

        Assert.Contains(Keys(rendered, "$unset"), k => k.Equals("dependencyRanges", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Keys(rendered, "$unset"), k => k.Equals("ckLanguage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Keys(rendered, "$set"), k => k.Equals("dependencyRanges", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RangeRetainingModel_SetsCkV2Members()
    {
        var rendered = Render(new CkModel
        {
            Id = new CkModelId("Range-1.0.0"), ModelId = "Range", CkLanguage = 2,
            DependencyRanges = [new CkModelDependency { Range = "System-[2.5,3.0)", Floor = "2.5.0" }]
        });

        Assert.Contains(Keys(rendered, "$set"), k => k.Equals("dependencyRanges", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Keys(rendered, "$set"), k => k.Equals("ckLanguage", StringComparison.OrdinalIgnoreCase));
    }

    // AB#6674: a full update writes the reason of a failed model and unsets it otherwise, never an explicit null.
    [Fact]
    public void ResolveFailedReason_IsSetWhenPresent_AndUnsetOtherwise()
    {
        var failed = Render(new CkModel
        {
            Id = new CkModelId("Failed-1.0.0"), ModelId = "Failed", ModelState = ModelState.ResolveFailed,
            ResolveFailedReason = "Unmet dependency: System-[2.5,3.0) (floor 2.5.0): installed System-2.4.0"
        });
        Assert.Contains(Keys(failed, "$set"), k => k.Equals("resolveFailedReason", StringComparison.OrdinalIgnoreCase));

        var healthy = Render(new CkModel { Id = new CkModelId("Healthy-1.0.0"), ModelId = "Healthy" });
        Assert.Contains(Keys(healthy, "$unset"), k => k.Equals("resolveFailedReason", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Keys(healthy, "$set"), k => k.Equals("resolveFailedReason", StringComparison.OrdinalIgnoreCase));
    }

    // Review N9: same pattern on CkType.methods.
    [Fact]
    public void ClassicType_UnsetsMethods_InsteadOfWritingNull()
    {
        var update = new CkTypeMongoDataSourceMapper().ApplyUpdate(new CkType
        {
            CkTypeId = new CkId<CkTypeId>("Classic-1.0.0/Thing-1"), CkModelId = new CkModelId("Classic-1.0.0")
        });
        var rendered = update.Render(new RenderArgs<CkType>(BsonSerializer.LookupSerializer<CkType>(),
            BsonSerializer.SerializerRegistry)).AsBsonDocument;

        Assert.Contains(Keys(rendered, "$unset"), k => k.Equals("methods", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Keys(rendered, "$set"), k => k.Equals("methods", StringComparison.OrdinalIgnoreCase));
    }
}
