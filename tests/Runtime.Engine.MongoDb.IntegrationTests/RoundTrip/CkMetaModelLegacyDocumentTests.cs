using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 (AB#5667 / AB#5668 / AB#5669, Phase 1 AB#5915), document level, no database: the new meta-model members
///     (<c>CkModel.ckLanguage</c>, <c>CkTypeAttribute.access</c>, <c>CkType.methods</c>) and the two new
///     collections (<c>CkInterface</c>, <c>CkTypeInterfaceImplementation</c>) against the BSON class maps.
///     <list type="number">
///         <item>A classic (v1) entity serializes to EXACTLY the pre-v2 element set — no new element appears.</item>
///         <item>A document written before CK v2 (no new elements) deserializes to <c>null</c> / defaults.</item>
///         <item>Declared values are written and read back verbatim, including every method field.</item>
///     </list>
///     Template: <see cref="CkAttributeOwnershipLegacyDocumentTests" />. The class maps come from the
///     assembly-wide <c>MongoDriverRegistrationFixture</c>.
/// </summary>
public class CkMetaModelLegacyDocumentTests
{
    private static readonly CkModelId ModelId = new("KitchenSink-1.0.0");

    // ---- 1. classic shape ---------------------------------------------------------------------

    [Fact]
    public void ClassicCkModel_KeepsThePreV2ElementSet()
    {
        var document = new CkModel
        {
            Id = ModelId, ModelId = ModelId.Name, Dependencies = [new CkModelId("System-2.5.0")],
            Description = "classic"
        }.ToBsonDocument();

        Assert.Equal(["_id", "modelId", "modelState", "dependencies", "description"],
            document.Names.ToArray());
    }

    [Fact]
    public void ClassicCkTypeAttribute_KeepsThePreV2ElementSet()
    {
        var document = NewTypeAttribute(null).ToBsonDocument();

        Assert.Equal(["_id", "attributeName", "isOptional"], document.Names.ToArray());
    }

    [Fact]
    public void ClassicCkType_HasNoMethodsElement()
    {
        var document = NewType(null).ToBsonDocument();

        Assert.DoesNotContain("methods", document.Names);
        Assert.DoesNotContain("implements", document.Names);
    }

    // ---- 2. documents written before CK v2 ----------------------------------------------------

    [Fact]
    public void PreV2Documents_ReadBackAsNullOrDefault()
    {
        var model = BsonSerializer.Deserialize<CkModel>(new BsonDocument
        {
            { "_id", "Legacy-1.0.0" }, { "modelId", "Legacy" }, { "modelState", 1 }
        });
        Assert.Null(model.CkLanguage);

        var typeAttribute = BsonSerializer.Deserialize<CkTypeAttribute>(new BsonDocument
        {
            { "_id", "Legacy-1.0.0/Name-1" }, { "attributeName", "Name" }, { "isOptional", true }
        });
        Assert.Null(typeAttribute.Access);
        Assert.Equal(CkAttributeAccessDto.ReadWrite, AttributeAccess.Resolve(typeAttribute.Access));

        var typeDocument = NewType(null).ToBsonDocument();
        var type = BsonSerializer.Deserialize<CkType>(typeDocument);
        Assert.Null(type.Methods);
    }

    // ---- 3. declared values round-trip --------------------------------------------------------

    [Fact]
    public void CkModel_CkLanguage_RoundTrips()
    {
        var document = new CkModel { Id = ModelId, ModelId = ModelId.Name, CkLanguage = 2 }.ToBsonDocument();

        Assert.Equal(2, document["ckLanguage"].AsInt32);
        Assert.Equal(2, BsonSerializer.Deserialize<CkModel>(document).CkLanguage);
    }

    [Theory]
    [InlineData(CkAttributeAccessDto.ReadWrite)]
    [InlineData(CkAttributeAccessDto.ReadOnly)]
    [InlineData(CkAttributeAccessDto.MethodOnly)]
    [InlineData(CkAttributeAccessDto.Hidden)]
    public void CkTypeAttribute_DeclaredAccess_RoundTrips(CkAttributeAccessDto access)
    {
        var document = NewTypeAttribute(access).ToBsonDocument();

        // Every DECLARED value is persisted, ReadWrite included: the member is nullable, so its default is
        // null (undeclared), not ReadWrite — a declared ReadWrite reads back as ReadWrite, not as null, and the
        // compiled DTO round-trips exactly. Wire format is Int32 (the driver default, like ownership):
        // CkAttributeAccessDto may only ever be appended to.
        Assert.Equal((int)access, document["access"].AsInt32);

        var readBack = BsonSerializer.Deserialize<CkTypeAttribute>(document);
        Assert.Equal(access, readBack.Access);
    }

    [Fact]
    public void CkType_MethodsWithEveryField_RoundTripVerbatim()
    {
        var methods = new List<CkMethodDto>
        {
            RoundTripMethods.Full(ModelId), new() { MethodId = "Ping-1" }, RoundTripMethods.Static()
        };
        var document = NewType(methods).ToBsonDocument();

        // The minimal method writes only its id — every optional member is left out.
        var minimal = document["methods"].AsBsonArray[1].AsBsonDocument;
        Assert.Equal(["methodId"], minimal.Names.ToArray());

        var readBack = BsonSerializer.Deserialize<CkType>(document);
        Assert.NotNull(readBack.Methods);
        Assert.Equal(3, readBack.Methods.Count);
        Assert.Equal((int)CkMethodKindDto.Static, document["methods"].AsBsonArray[2]["kind"].AsInt32);
        AssertSameMethod(RoundTripMethods.Static(), readBack.Methods[2]);
        AssertSameMethod(RoundTripMethods.Full(ModelId), readBack.Methods[0]);
        AssertSameMethod(new CkMethodDto { MethodId = "Ping-1" }, readBack.Methods[1]);
    }

    [Fact]
    public void CkInterface_RoundTrips()
    {
        var entity = new CkInterface
        {
            CkInterfaceId = new CkId<CkInterfaceId>(ModelId, new CkInterfaceId("Named-1")),
            CkModelId = ModelId,
            Description = "named things",
            Attributes =
            [
                new CkInterfaceAttribute
                {
                    AttributeId = new CkId<CkAttributeId>("System-2.5.0/Name-1"), AttributeName = "Name"
                },
                new CkInterfaceAttribute
                {
                    AttributeId = new CkId<CkAttributeId>(ModelId, new CkAttributeId("Alias-1")),
                    AttributeName = "Alias", IsOptional = true
                }
            ]
        };
        var document = entity.ToBsonDocument();

        Assert.Equal("KitchenSink-1.0.0/Named-1", document["_id"].AsString);
        Assert.DoesNotContain("isOptional", document["attributes"].AsBsonArray[0].AsBsonDocument.Names);

        var readBack = BsonSerializer.Deserialize<CkInterface>(document);
        Assert.Equal(entity.CkInterfaceId, readBack.CkInterfaceId);
        Assert.Equal(ModelId, readBack.CkModelId);
        Assert.Equal("named things", readBack.Description);
        Assert.Collection(readBack.Attributes,
            a =>
            {
                Assert.Equal("System-2.5.0/Name-1", a.AttributeId.FullName);
                Assert.Equal("Name", a.AttributeName);
                Assert.False(a.IsOptional);
            },
            a =>
            {
                Assert.Equal("KitchenSink-1.0.0/Alias-1", a.AttributeId.FullName);
                Assert.True(a.IsOptional);
            });
    }

    [Fact]
    public void CkTypeInterfaceImplementation_RoundTrips_IncludingMajorQualifiedReference()
    {
        var entity = new CkTypeInterfaceImplementation
        {
            ImplementationId = OctoObjectId.GenerateNewId(),
            CkModelId = ModelId,
            CkTypeId = new CkId<CkTypeId>(ModelId, new CkTypeId("Gadget-1")),
            // A range-retaining model persists its references major-qualified, verbatim (AB#5665).
            CkInterfaceId = new CkId<CkInterfaceId>("RrBase@1/Named-1")
        };
        var document = entity.ToBsonDocument();

        Assert.Equal("RrBase@1/Named-1", document["ckInterfaceId"].AsString);

        var readBack = BsonSerializer.Deserialize<CkTypeInterfaceImplementation>(document);
        Assert.Equal(entity.ImplementationId, readBack.ImplementationId);
        Assert.Equal(entity.CkTypeId, readBack.CkTypeId);
        Assert.Equal(entity.CkInterfaceId, readBack.CkInterfaceId);
    }

    // ---- Phase 1 members (F1.3-S2, AB#5915) ------------------------------------------------------------

    private static readonly string[] Phase1Elements =
        ["visibility", "derivable", "targetCkInterfaceId", "extends", "associations", "deprecated", "minEngineVersion",
            "securitySensitive"];

    [Fact]
    public void ClassicElements_WriteNoPhase1Element()
    {
        var documents = new[]
        {
            new CkModel { Id = ModelId, ModelId = ModelId.Name }.ToBsonDocument(),
            NewType(null).ToBsonDocument(),
            new CkRecord { CkModelId = ModelId, CkRecordId = new CkId<CkRecordId>(ModelId, new CkRecordId("Address-1")) }
                .ToBsonDocument(),
            new CkEnum { CkModelId = ModelId, CkEnumId = new CkId<CkEnumId>(ModelId, new CkEnumId("Mode-1")) }.ToBsonDocument(),
            new CkAttribute
            {
                CkModelId = ModelId, CkAttributeId = new CkId<CkAttributeId>(ModelId, new CkAttributeId("Name-1")),
                AttributeValueType = AttributeValueTypesDto.String
            }.ToBsonDocument(),
            new CkAssociationRole
            {
                CkModelId = ModelId, RoleId = new CkId<CkAssociationRoleId>(ModelId, new CkAssociationRoleId("Link-1")),
                InboundName = "LinkedFrom", OutboundName = "LinksTo"
            }.ToBsonDocument(),
            new CkTypeAssociation
            {
                CkModelId = ModelId, RoleId = new CkId<CkAssociationRoleId>(ModelId, new CkAssociationRoleId("Link-1")),
                OriginCkTypeId = new CkId<CkTypeId>(ModelId, new CkTypeId("Gadget-1")),
                TargetCkTypeId = new CkId<CkTypeId>(ModelId, new CkTypeId("Widget-1"))
            }.ToBsonDocument(),
            new CkInterface
            {
                CkInterfaceId = new CkId<CkInterfaceId>(ModelId, new CkInterfaceId("Named-1")), CkModelId = ModelId
            }.ToBsonDocument()
        };

        Assert.All(documents, d => Assert.Empty(d.Names.Intersect(Phase1Elements)));
    }

    [Fact]
    public void Phase1Members_RoundTripVerbatim()
    {
        var type = NewType(null);
        type.Visibility = CkVisibilityDto.Internal;
        type.Derivable = CkDerivableDto.Model;
        var typeBack = BsonSerializer.Deserialize<CkType>(type.ToBsonDocument());
        Assert.Equal((CkVisibilityDto.Internal, CkDerivableDto.Model), (typeBack.Visibility, typeBack.Derivable));

        var association = new CkTypeAssociation
        {
            CkModelId = ModelId, RoleId = new CkId<CkAssociationRoleId>(ModelId, new CkAssociationRoleId("Link-1")),
            OriginCkTypeId = new CkId<CkTypeId>(ModelId, new CkTypeId("Gadget-1")),
            TargetCkTypeId = new CkId<CkTypeId>(ModelId, new CkTypeId("Widget-1")),
            TargetCkInterfaceId = new CkId<CkInterfaceId>(ModelId, new CkInterfaceId("Named-1"))
        };
        Assert.Equal(association.TargetCkInterfaceId,
            BsonSerializer.Deserialize<CkTypeAssociation>(association.ToBsonDocument()).TargetCkInterfaceId);

        var entity = new CkInterface
        {
            CkInterfaceId = new CkId<CkInterfaceId>(ModelId, new CkInterfaceId("Labeled-1")),
            CkModelId = ModelId,
            Extends = [new CkId<CkInterfaceId>(ModelId, new CkInterfaceId("Named-1"))],
            Associations =
            [
                new CkInterfaceAssociationDto
                {
                    CkRoleId = new CkId<CkAssociationRoleId>(ModelId, new CkAssociationRoleId("Link-1")),
                    TargetCkInterfaceId = new CkId<CkInterfaceId>(ModelId, new CkInterfaceId("Named-1")),
                    Multiplicity = MultiplicitiesDto.N, IsOptional = true
                }
            ],
            Methods = [new CkMethodDto { MethodId = "Relabel-1", Visibility = CkVisibilityDto.Internal }],
            Deprecated = true,
            Visibility = CkVisibilityDto.Internal
        };
        var back = BsonSerializer.Deserialize<CkInterface>(entity.ToBsonDocument());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(entity), System.Text.Json.JsonSerializer.Serialize(back));
    }

    [Fact]
    public void UsedSurface_RoundTripsVerbatim_AndLegacyDependencyDocumentsReadAsNull()
    {
        // CK v2 (AB#4472)
        var dependency = new CkModelDependency
        {
            Range = "System-[2.5,3.0)", Floor = "2.5.0", UsedSurface = ["System@2/Entity-1", "System@2/Entity-1.Name"],
            UsedSurfaceHash = "sha256:" + new string('a', 64)
        };
        var back = BsonSerializer.Deserialize<CkModelDependency>(dependency.ToBsonDocument());
        Assert.Equal(dependency.UsedSurface, back.UsedSurface);
        Assert.Equal(dependency.UsedSurfaceHash, back.UsedSurfaceHash);

        var legacy = new BsonDocument { ["range"] = "System-[2.5,3.0)", ["floor"] = "2.5.0" };
        var legacyBack = BsonSerializer.Deserialize<CkModelDependency>(legacy);
        Assert.Null(legacyBack.UsedSurface);
        Assert.Null(legacyBack.UsedSurfaceHash);
        Assert.DoesNotContain("usedSurface", new CkModelDependency { Range = "System-[2.5,3.0)", Floor = "2.5.0" }.ToBsonDocument().Names);
    }

    [Fact]
    public void SecuritySensitive_RoundTripsVerbatim_AndIsAbsentWhenUndeclared()
    {
        // CK v2 (AB#6269)
        CkAttribute NewAttribute(bool? securitySensitive) => new()
        {
            CkAttributeId = new CkId<CkAttributeId>(ModelId, new CkAttributeId("PasswordHash-1")), CkModelId = ModelId,
            AttributeValueType = AttributeValueTypesDto.String, SecuritySensitive = securitySensitive
        };

        var marked = NewAttribute(true).ToBsonDocument();
        Assert.True(marked["securitySensitive"].AsBoolean);
        Assert.True(BsonSerializer.Deserialize<CkAttribute>(marked).SecuritySensitive);

        var undeclared = NewAttribute(null).ToBsonDocument();
        Assert.DoesNotContain("securitySensitive", undeclared.Names);
        Assert.Null(BsonSerializer.Deserialize<CkAttribute>(undeclared).SecuritySensitive);
    }

    private static void AssertSameMethod(CkMethodDto expected, CkMethodDto actual)
    {
        // Field by field through the JSON the engine uses for compiled models — a member the class map
        // drops shows up as a difference here.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected),
            System.Text.Json.JsonSerializer.Serialize(actual));
    }

    private static CkTypeAttribute NewTypeAttribute(CkAttributeAccessDto? access) =>
        new()
        {
            AttributeId = new CkId<CkAttributeId>(ModelId, new CkAttributeId("Name-1")),
            AttributeName = "Name",
            IsOptional = true,
            Access = access
        };

    private static CkType NewType(List<CkMethodDto>? methods) =>
        new()
        {
            CkModelId = ModelId,
            CkTypeId = new CkId<CkTypeId>(ModelId, new CkTypeId("Gadget-1")),
            CollectionName = "Gadget",
            Attributes = [NewTypeAttribute(null)],
            Methods = methods
        };
}
