using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     AB#9524: index attribute paths resolve against the inheritance chain of the index-defining type. Invariant: a path
///     that resolved against the type's own attributes resolves exactly as before; a plain inherited attribute resolves to
///     the very string the fallback produced (no key spec change, no rebuild). One test per abuse case.
/// </summary>
public class IndexAttributePathsTests
{
    private const string Model = "IdxChain-1.0.0";

    private readonly Dictionary<CkId<CkAttributeId>, CkAttribute> _attributes = new();
    private readonly Dictionary<CkId<CkRecordId>, CkRecord> _records = new();

    public IndexAttributePathsTests()
    {
        Define("Name", AttributeValueTypesDto.String);
        Define("Label", AttributeValueTypesDto.String);
        Define("LabelRecord", AttributeValueTypesDto.Record, "Span");
        Define("SpanAttr", AttributeValueTypesDto.Record, "Span");
        Define("From", AttributeValueTypesDto.DateTime);
        Define("Tags", AttributeValueTypesDto.StringArray);
        Define("ApiKey", AttributeValueTypesDto.Secret);
        Define("PasswordHash", AttributeValueTypesDto.String);
        _records[RecordId("Span")] = new CkRecord { CkRecordId = RecordId("Span"), Attributes = [Assign("From", "From")] };
    }

    // ---- AC 1 / AC 5: inherited plain attribute ----------------------------------------------------------------------

    [Theory]
    [InlineData("Name")]
    [InlineData("name")]
    public void InheritedPlainAttribute_ResolvesWithoutFallback_ToTheExactFallbackString(string writtenPath)
    {
        var (provider, hidden) = ProvidersFor(Chain(Type("Base", Assign("Name", "Name")), Type("Process")));

        var (fields, log) = Resolve(provider, hidden, writtenPath);

        // Byte-identical to the key spec the fallback produced before AB#9524: no index rebuild on existing tenants.
        Assert.Equal(["attributes.name"], fields);
        Assert.Equal(IndexAttributePaths.FallbackPath(writtenPath), fields.Single());
        Assert.Empty(log);
    }

    [Fact]
    public void TypeOwnAttributesOnly_StillFallBack_AsBefore()
    {
        // Without the chain (the behaviour before AB#9524) the inherited attribute is not found: the fallback runs.
        var (provider, hidden) = ProvidersFor([Type("Process")]);

        var (fields, log) = Resolve(provider, hidden, "Name");

        Assert.Equal(["attributes.name"], fields);
        Assert.Equal(["fallback:Name"], log);
    }

    [Fact]
    public void AttributeInheritedOverTwoLevels_Resolves()
    {
        var (provider, hidden) = ProvidersFor(Chain(Type("Root", Assign("Name", "Name")), Type("Mid"), Type("Leaf")));

        var (fields, log) = Resolve(provider, hidden, "Name");

        Assert.Equal(["attributes.name"], fields);
        Assert.Empty(log);
    }

    // ---- AC 4: inherited record and array paths ------------------------------------------------------------------------

    [Fact]
    public void InheritedRecordPath_ResolvesToTheStoredCamelCasePath()
    {
        var (provider, hidden) = ProvidersFor(Chain(Type("Base", Assign("SpanAttr", "TimeRange")), Type("Process")));

        var (fields, log) = Resolve(provider, hidden, "TimeRange.From");

        Assert.Equal(["attributes.timeRange.attributes.from"], fields);
        Assert.Empty(log);
        // The fallback of before pointed at a field that is never stored (accepted change, Odin 2026-11-24).
        Assert.Equal("attributes.timeRange.From", IndexAttributePaths.FallbackPath("TimeRange.From"));
    }

    [Fact]
    public void InheritedArrayPath_ResolvesLikeAnOwnArrayAttribute()
    {
        var own = Resolve(ProvidersFor([Type("Process", Assign("Tags", "Tags"))]), "Tags[*]");
        var inherited = Resolve(ProvidersFor(Chain(Type("Base", Assign("Tags", "Tags")), Type("Process"))), "Tags[*]");

        Assert.Equal(["attributes.tags"], own.Fields);
        Assert.Equal(own.Fields, inherited.Fields);
        Assert.Empty(inherited.Log);
    }

    // ---- invariant: the type's own assignment wins ---------------------------------------------------------------------

    [Fact]
    public void OwnAssignmentWins_OverABaseAssignmentOfTheSameNameInAnotherCasing_RecordOnTheDerived()
    {
        // Base: Label is a String; the derived type assigns "label" as a record. The derived one decides, as before.
        var chain = Chain(Type("Base", Assign("Label", "Label")), Type("Process", Assign("LabelRecord", "label")));

        var (fields, log) = Resolve(ProvidersFor(chain), "Label.From");

        Assert.Equal(["attributes.label.attributes.from"], fields);
        Assert.Empty(log);
    }

    [Fact]
    public void OwnAssignmentWins_OverABaseRecordOfTheSameName_ResultUnchangedFromBefore()
    {
        // Base: Label is a record; the derived type assigns Label as a String. "Label.From" did not resolve against the
        // derived type alone and must not start resolving through the shadowed base record.
        var chain = Chain(Type("Base", Assign("LabelRecord", "Label")), Type("Process", Assign("Label", "Label")));
        var before = Resolve(ProvidersFor([chain[^1]]), "Label.From");

        var after = Resolve(ProvidersFor(chain), "Label.From");

        Assert.Equal(before.Fields, after.Fields);
        Assert.Equal(["attributes.label.From"], after.Fields);
        Assert.Equal(["fallback:Label.From"], after.Log);
    }

    [Fact]
    public void OwnAttributes_ResolveExactlyAsWithoutTheChain()
    {
        var process = Type("Process", Assign("Name", "Name"), Assign("SpanAttr", "TimeRange"), Assign("Tags", "Tags"));
        string[] paths = ["Name", "TimeRange.From", "Tags[*]", "Unknown", "rtId"];
        var before = Resolve(ProvidersFor([process]), paths);

        var after = Resolve(ProvidersFor(Chain(Type("Base", Assign("Label", "Label")), process)), paths);

        Assert.Equal(before.Fields, after.Fields);
        Assert.Equal(before.Log, after.Log);
    }

    // ---- AC 2: Secret and Hidden on inherited assignments --------------------------------------------------------------

    [Fact]
    public void InheritedSecretAttribute_IsSkipped()
    {
        var (provider, hidden) = ProvidersFor(Chain(Type("Base", Assign("ApiKey", "ApiKey")), Type("Process")));

        var (fields, log) = Resolve(provider, hidden, "ApiKey", "Name");

        Assert.DoesNotContain(fields, f => f.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("secret:ApiKey", log);
    }

    [Fact]
    public void HiddenAssignmentOnABaseType_IsSkipped_ThroughTheChainOfTheDerivedType()
    {
        // Only the derived type's provider is consulted: its chain carries the base's Hidden assignment.
        var chain = Chain(Type("Base", Assign("PasswordHash", "PasswordHash", CkAttributeAccessDto.Hidden)),
            Type("Process", Assign("Name", "Name")));
        var provider = IndexAttributePaths.ProviderFor(chain, _attributes, _records);

        var (fields, log) = Resolve(provider, [provider], "PasswordHash", "Name");

        Assert.Equal(["attributes.name"], fields);
        Assert.Equal(["hidden:PasswordHash"], log);
    }

    [Fact]
    public void HiddenBaseAssignment_WinsOverANonHiddenCaseVariantOnTheDerivedType()
    {
        var chain = Chain(Type("Base", Assign("PasswordHash", "PasswordHash", CkAttributeAccessDto.Hidden)),
            Type("Process", Assign("PasswordHash", "passwordHash")));
        var provider = IndexAttributePaths.ProviderFor(chain, _attributes, _records);

        var (fields, log) = Resolve(provider, [provider], "passwordHash");

        Assert.Empty(fields);
        Assert.Equal(["hidden:passwordHash"], log);
    }

    // ---- AC 3: unresolvable path ---------------------------------------------------------------------------------------

    [Fact]
    public void UnresolvablePath_FallsBackOnce_WithTheUnchangedFallbackPath()
    {
        var (provider, hidden) = ProvidersFor(Chain(Type("Base", Assign("Name", "Name")), Type("Process")));

        var (fields, log) = Resolve(provider, hidden, "Missing", "Name");

        Assert.Equal(["attributes.missing", "attributes.name"], fields);
        Assert.Equal(["fallback:Missing"], log);
    }

    // ---- type chains ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BuildTypeChains_BaseTypesRootAndDescendants_MostBaseFirst()
    {
        var entity = Type("Entity");
        var named = Type("Named");
        var root = Root("Process");
        var step = Type("Step");
        var subStep = Type("SubStep");
        root.Inheritances = [step, subStep];
        root.InheritedTypes = [Edge(subStep, step), Edge(step, root)];

        var chains = IndexAttributePaths.BuildTypeChains([entity, named], root);

        Assert.Equal(Expected("Entity"), Names(chains[entity.CkTypeId]));
        Assert.Equal(Expected("Entity", "Named"), Names(chains[named.CkTypeId]));
        Assert.Equal(Expected("Entity", "Named", "Process"), Names(chains[root.CkTypeId]));
        Assert.Equal(Expected("Entity", "Named", "Process", "Step"), Names(chains[step.CkTypeId]));
        Assert.Equal(Expected("Entity", "Named", "Process", "Step", "SubStep"), Names(chains[subStep.CkTypeId]));
    }

    [Fact]
    public void BuildTypeChains_ACycleOrAParentOutsideTheCollection_EndsTheChain()
    {
        var root = Root("Process");
        var a = Type("A");
        var b = Type("B");
        var orphan = Type("Orphan");
        root.Inheritances = [a, b, orphan];
        root.InheritedTypes = [Edge(a, b), Edge(b, a), Edge(orphan, Type("Elsewhere"))];

        var chains = IndexAttributePaths.BuildTypeChains([], root);

        Assert.Equal(Expected("Process"), Names(chains[root.CkTypeId]));
        Assert.Equal(Expected("A", "B"), Names(chains[b.CkTypeId]));
        Assert.Equal(Expected("B", "A"), Names(chains[a.CkTypeId]));
        Assert.Equal(Expected("Orphan"), Names(chains[orphan.CkTypeId]));
    }

    [Fact]
    public void ChainOf_WithoutAKnownChain_IsTheTypeAlone()
    {
        var type = Type("Process");

        Assert.Same(type, Assert.Single(IndexAttributePaths.ChainOf(type, null)));
        Assert.Same(type, Assert.Single(IndexAttributePaths.ChainOf(type,
            new Dictionary<CkId<CkTypeId>, IReadOnlyList<CkType>>())));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private (List<string> Fields, List<string> Log) Resolve(
        (DatabaseAttributeMetadataProvider Provider, List<DatabaseAttributeMetadataProvider> Hidden) providers,
        params string[] paths) => Resolve(providers.Provider, providers.Hidden, paths);

    private static (List<string> Fields, List<string> Log) Resolve(DatabaseAttributeMetadataProvider provider,
        List<DatabaseAttributeMetadataProvider> hidden, params string[] paths)
    {
        var log = new List<string>();
        var fields = IndexAttributePaths.ResolveFields(paths, provider, hidden,
            n => log.Add("secret:" + n), n => log.Add("hidden:" + n), n => log.Add("fallback:" + n));
        return (fields, log);
    }

    private (DatabaseAttributeMetadataProvider Provider, List<DatabaseAttributeMetadataProvider> Hidden) ProvidersFor(
        IReadOnlyList<CkType> chain)
    {
        var provider = IndexAttributePaths.ProviderFor(chain, _attributes, _records);
        return (provider, [provider]);
    }

    private static List<CkType> Chain(params CkType[] mostBaseFirst) => mostBaseFirst.ToList();

    private void Define(string id, AttributeValueTypesDto valueType, string? record = null) =>
        _attributes[AttributeId(id)] = new CkAttribute
        {
            CkAttributeId = AttributeId(id), AttributeValueType = valueType,
            ValueCkRecordId = record == null ? null : RecordId(record)
        };

    private static CkTypeAttribute Assign(string id, string name, CkAttributeAccessDto? access = null) =>
        new() { AttributeId = AttributeId(id), AttributeName = name, Access = access };

    private static CkType Type(string name, params CkTypeAttribute[] attributes) =>
        new() { CkTypeId = TypeId(name), Attributes = attributes.ToList() };

    private static CkTypeInfo Root(string name) => new() { CkTypeId = TypeId(name), IsCollectionRoot = true };

    private static CkInheritedTypeInfo Edge(CkType inheritor, CkType baseType) =>
        new() { InheritorCkTypeId = inheritor.CkTypeId, BaseCkTypeId = baseType.CkTypeId };

    private static IEnumerable<string> Names(IEnumerable<CkType> chain) =>
        chain.Select(t => t.CkTypeId.ToString());

    private static string[] Expected(params string[] names) => names.Select(n => TypeId(n).ToString()).ToArray();

    private static CkId<CkTypeId> TypeId(string name) => new($"{Model}/{name}");
    private static CkId<CkAttributeId> AttributeId(string name) => new($"{Model}/{name}");
    private static CkId<CkRecordId> RecordId(string name) => new($"{Model}/{name}");
}
