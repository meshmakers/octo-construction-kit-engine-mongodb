using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 F1.3-S3 (AB#5916) — the HARD GATE of concept §4.6 guard 1: every compiled model this repository has is
///     imported (<c>ExecuteImport</c>), read back (<c>TryLookupCkModelAsync</c>, the path the runtime CK cache is
///     rebuilt from) and compared with the compiled DTO by reflection over all public properties
///     (<see cref="CkModelReflectionComparer" />). A DTO property that persistence does not write and read back fails
///     this test without anybody listing it. Intentionally unpersisted properties are in
///     <see cref="CkModelReflectionComparer.AllowList" />, each with a reason.
///     <para>
///         <b>Mutation check</b> (<see cref="UnpersistedDtoProperty_FailsTheGate" />): a compiled type DTO carrying a
///         property the persistence layer does not know — a stand-in for "a developer added a property to
///         <c>CkTypeDto</c> and forgot the Mongo side" — goes through the real import and read-back and the gate
///         reports it. Verified manually as well by temporarily removing a read-back mapping (e.g.
///         <c>Visibility = t.Visibility</c> in <c>TryLookupCkModelAsync</c>): the gate fails with the property path.
///     </para>
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkMetaModelReflectionGateTests(CkModelImportMigrationFixture fixture)
{
    public static TheoryData<string> CatalogModels() =>
    [
        "System",
        "System.StreamData",
        "Test-1.0.0",
        "Test-2.0.0",
        "KitchenSink-1.0.0"
    ];

    [Theory]
    [MemberData(nameof(CatalogModels))]
    public async Task CompiledModel_SurvivesTheMongoRoundTrip_ByReflection(string model)
    {
        var helper = new CkMetaModelRoundTripTests(fixture);
        await helper.WithThrowawayTenantAsync("rtgate", async (tenant, tenantId) =>
        {
            var catalogService = fixture.GetService<ICatalogService>();
            CkModelId modelId;
            if (model == "System")
            {
                modelId = await helper.GetInstalledSystemIdAsync(tenant);
            }
            else if (model.Contains('-'))
            {
                modelId = new CkModelId(model);
            }
            else
            {
                var versions = await catalogService.ListVersionsAsync(model);
                Assert.NotEmpty(versions);
                modelId = versions.OrderByDescending(v => v.ModelId).First().ModelId;
            }

            var compiled = await catalogService.GetAsync(modelId, new OperationResult());
            Assert.NotNull(compiled);
            if (model != "System")
            {
                await tenant.ImportCkModelAsync(compiled);
            }

            await helper.AssertRoundTripAsync(tenantId, compiled);
        });
    }

    [Fact]
    public async Task CSharpKitchenSink_SurvivesTheMongoRoundTrip_ByReflection()
    {
        var helper = new CkMetaModelRoundTripTests(fixture);
        await helper.WithThrowawayTenantAsync("rtgatecs", async (tenant, tenantId) =>
        {
            var compiled = CkV2KitchenSinkModel.Build(await helper.GetInstalledSystemIdAsync(tenant));
            await tenant.ImportCkModelAsync(compiled);
            await helper.AssertRoundTripAsync(tenantId, compiled);
        });
    }

    /// <summary>
    ///     The mutation check of the acceptance criteria, automated: a type DTO with an extra property (a stand-in for
    ///     a new engine DTO member) survives the import — persistence simply ignores what it does not know — and the
    ///     reflection gate reports it on the read-back.
    /// </summary>
    [Fact]
    public async Task UnpersistedDtoProperty_FailsTheGate()
    {
        var helper = new CkMetaModelRoundTripTests(fixture);
        await helper.WithThrowawayTenantAsync("rtgatemut", async (tenant, tenantId) =>
        {
            var compiled = CkV2KitchenSinkModel.Build(await helper.GetInstalledSystemIdAsync(tenant));
            var widget = compiled.Types!.Single(t => t.TypeId.Name == "Widget");
            compiled.Types!.Remove(widget);
            compiled.Types!.Add(new TypeDtoWithNewMemberDto
            {
                TypeId = widget.TypeId, IsCollectionRoot = widget.IsCollectionRoot,
                DerivedFromCkTypeId = widget.DerivedFromCkTypeId, Implements = widget.Implements,
                Attributes = widget.Attributes, Visibility = widget.Visibility, NewMember = "not persisted"
            });
            await tenant.ImportCkModelAsync(compiled);

            var readBack = await helper.LookupAsync(tenantId, compiled.ModelId);
            var differences = CkModelReflectionComparer.Compare(compiled, readBack!);

            var difference = Assert.Single(differences);
            Assert.Contains("NewMember", difference);
            Assert.Contains("only on the compiled side", difference);
        });
    }

    /// <summary>A compiled type DTO with one property the persistence layer does not know.</summary>
    private sealed class TypeDtoWithNewMemberDto : CkCompiledTypeDto
    {
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        public string? NewMember { get; init; }
    }
}
