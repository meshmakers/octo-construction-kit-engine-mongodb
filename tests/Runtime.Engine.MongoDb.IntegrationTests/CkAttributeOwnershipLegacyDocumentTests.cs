using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// AB#5187, document level. Companion to <see cref="CkAttributeOwnershipPersistenceTests" />: these
/// assert the BSON class maps directly, with no database and no import, so the two questions the
/// round-trip test cannot isolate get answered explicitly.
/// <list type="number">
///     <item>
///         Ownership is written and read back verbatim on both the attribute DEFINITION and the
///         per-assignment override.
///     </item>
///     <item>
///         A document written by an engine that predates AB#5187 has NO <c>ownership</c> element.
///         It must keep resolving through the persisted <c>isRuntimeState</c> mirror — a flagged
///         attribute stays preserved-on-upsert. Falling back to <c>SeedOwned</c> instead would let
///         the next blueprint re-apply overwrite live credentials.
///     </item>
/// </list>
/// The class maps come from the assembly-wide <c>MongoDriverRegistrationFixture</c>, so no
/// MongoDB server is involved (same pattern as <see cref="AttributeArrayValuePolymorphicWrapperTests" />).
/// </summary>
public class CkAttributeOwnershipLegacyDocumentTests
{
    private const string OwnershipElementName = "ownership";
    private const string IsRuntimeStateElementName = "isRuntimeState";
    private static readonly CkModelId TestModelId = new("Test-1.0.0");

    public static TheoryData<AttributeOwnershipDto> AllOwnershipValues() =>
    [
        AttributeOwnershipDto.SeedOwned,
        AttributeOwnershipDto.TenantOwned,
        AttributeOwnershipDto.RuntimeState,
        AttributeOwnershipDto.Secret
    ];

    [Theory]
    [MemberData(nameof(AllOwnershipValues))]
    public void CkAttribute_DeclaredOwnership_SurvivesBsonRoundTrip(AttributeOwnershipDto ownership)
    {
        var document = NewAttribute(ownership, ownership.IsPreservedOnUpsert()).ToBsonDocument();

        Assert.True(document.Contains(OwnershipElementName),
            "the declared ownership must be persisted, otherwise the read side silently falls back to the boolean mirror");
        // Wire format: the driver's default enum representation (the underlying Int32), same as
        // every other enum in the CK class maps. Pinned because the numbers ARE the stored value:
        // AttributeOwnershipDto may only ever be appended to, never reordered.
        Assert.Equal(BsonType.Int32, document[OwnershipElementName].BsonType);
        Assert.Equal((int)ownership, document[OwnershipElementName].AsInt32);

        var readBack = BsonSerializer.Deserialize<CkAttribute>(document);

        Assert.Equal(ownership, readBack.Ownership);
        Assert.Equal(ownership.IsPreservedOnUpsert(), readBack.IsRuntimeState);
        Assert.Equal(ownership, AttributeOwnership.Resolve(readBack.Ownership, readBack.IsRuntimeState));
    }

    [Theory]
    [MemberData(nameof(AllOwnershipValues))]
    public void CkTypeAttribute_DeclaredOverride_SurvivesBsonRoundTrip(AttributeOwnershipDto ownership)
    {
        var document = NewTypeAttribute(ownership).ToBsonDocument();

        Assert.True(document.Contains(OwnershipElementName));

        var readBack = BsonSerializer.Deserialize<CkTypeAttribute>(document);

        Assert.Equal(ownership, readBack.Ownership);
        // The override wins over the definition — including when it widens a Secret definition
        // back to SeedOwned, which the boolean could not express at all.
        Assert.Equal(ownership,
            AttributeOwnership.Resolve(readBack.Ownership, AttributeOwnershipDto.Secret));
    }

    /// <summary>
    /// An undeclared ownership stays ABSENT from the document (the convention every other optional
    /// member in the CK class maps follows), so a current engine writes exactly the shape an older
    /// one reads.
    /// </summary>
    [Fact]
    public void UndeclaredOwnership_IsNotWrittenToTheDocument()
    {
        Assert.False(NewAttribute(null, false).ToBsonDocument().Contains(OwnershipElementName));
        Assert.False(NewTypeAttribute(null).ToBsonDocument().Contains(OwnershipElementName));
    }

    /// <summary>
    /// The version-skew case: a CkAttribute document written before AB#5187 carries only the
    /// boolean. <c>isRuntimeState: true</c> must resolve to <see cref="AttributeOwnershipDto.RuntimeState" />
    /// — today's behaviour — and never to <see cref="AttributeOwnershipDto.SeedOwned" />, which
    /// would hand a rotating token or a client secret back to the next seed re-apply.
    /// </summary>
    [Theory]
    [InlineData(true, AttributeOwnershipDto.RuntimeState)]
    [InlineData(false, AttributeOwnershipDto.SeedOwned)]
    public void CkAttribute_DocumentWrittenBeforeOwnershipExisted_ResolvesThroughTheBooleanMirror(
        bool isRuntimeState, AttributeOwnershipDto expected)
    {
        // Start from a fully marked document and strip the element an older engine never wrote.
        var document = NewAttribute(AttributeOwnershipDto.Secret, isRuntimeState).ToBsonDocument();
        document.Remove(OwnershipElementName);
        Assert.True(document.Contains(IsRuntimeStateElementName),
            "the mirror is what a legacy reader AND a legacy document rely on — it must stay persisted unconditionally");

        var readBack = BsonSerializer.Deserialize<CkAttribute>(document);

        Assert.Null(readBack.Ownership);
        Assert.Equal(isRuntimeState, readBack.IsRuntimeState);
        Assert.Equal(expected, AttributeOwnership.Resolve(readBack.Ownership, readBack.IsRuntimeState));
    }

    /// <summary>
    /// The assignment counterpart: a pre-AB#5187 <c>CkTypeAttribute</c> sub-document has no
    /// ownership element and never had a boolean either, so it must read back as "inherit from the
    /// definition" — not as an explicit <see cref="AttributeOwnershipDto.SeedOwned" /> that would
    /// override a Secret definition.
    /// </summary>
    [Fact]
    public void CkTypeAttribute_DocumentWrittenBeforeOwnershipExisted_InheritsFromTheDefinition()
    {
        var document = NewTypeAttribute(AttributeOwnershipDto.SeedOwned).ToBsonDocument();
        document.Remove(OwnershipElementName);

        var readBack = BsonSerializer.Deserialize<CkTypeAttribute>(document);

        Assert.Null(readBack.Ownership);
        Assert.Equal(AttributeOwnershipDto.Secret,
            AttributeOwnership.Resolve(readBack.Ownership, AttributeOwnershipDto.Secret));
    }

    private static CkAttribute NewAttribute(AttributeOwnershipDto? ownership, bool isRuntimeState) =>
        new()
        {
            CkModelId = TestModelId,
            CkAttributeId = new CkId<CkAttributeId>(TestModelId, "ApiKey"),
            AttributeValueType = AttributeValueTypesDto.String,
            Ownership = ownership,
            IsRuntimeState = isRuntimeState
        };

    private static CkTypeAttribute NewTypeAttribute(AttributeOwnershipDto? ownership) =>
        new()
        {
            AttributeId = new CkId<CkAttributeId>(TestModelId, "ApiKey"),
            AttributeName = "ApiKey",
            IsOptional = true,
            Ownership = ownership
        };
}
