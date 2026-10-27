using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>
///     CK language 2 test model of the interface query (AB#7019), built in C#:
///     <list type="bullet">
///         <item>Interfaces <c>Named-1</c> (Name, Rank), <c>Titled-1</c> (Name) and <c>Lonely-1</c> (Name, no implementer).</item>
///         <item>
///             Collection roots <c>Asset</c> (Named, Titled), <c>Sensor</c> (Named), <c>Valve</c> (Named) and <c>Vault</c> (Named);
///             <c>Pump</c> derives from Asset and <c>Gauge</c> from Sensor, so Asset+Pump and Sensor+Gauge share a collection
///             and the interface spans four collections.
///         </item>
///     </list>
/// </summary>
internal static class InterfaceQueryTestModel
{
    internal static readonly CkModelId ModelId = new("InterfaceQuery-1.0.0");

    internal static string TypeName(string type) => $"InterfaceQuery/{type}";

    internal static CkCompiledModelRoot Build(CkModelId systemId)
    {
        var id = ModelId;
        CkId<CkAttributeId> Attribute(string name) => new(id, new CkAttributeId($"{name}-1"));
        CkId<CkInterfaceId> Interface(string name) => new(id, new CkInterfaceId($"{name}-1"));

        CkTypeAttributeDto Assign(string name, bool isOptional) => new()
        {
            CkAttributeId = Attribute(name), AttributeName = name, IsOptional = isOptional
        };

        CkCompiledTypeDto Root(string name, params string[] interfaces) => new()
        {
            TypeId = new CkTypeId($"{name}-1"),
            IsCollectionRoot = true,
            DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
            Implements = interfaces.Select(Interface).ToList(),
            Attributes = [Assign("Name", false), Assign("Rank", true)]
        };

        CkCompiledTypeDto Derived(string name, string baseName) => new()
        {
            TypeId = new CkTypeId($"{name}-1"),
            DerivedFromCkTypeId = new CkId<CkTypeId>(id, new CkTypeId($"{baseName}-1")),
            Attributes = [Assign("Note", true)]
        };

        CkInterfaceDto InterfaceOf(string name, params (string Member, bool Optional)[] members) => new()
        {
            InterfaceId = new CkInterfaceId($"{name}-1"),
            Attributes = members.Select(m => new CkInterfaceAttributeDto
            {
                CkAttributeId = Attribute(m.Member), AttributeName = m.Member, IsOptional = m.Optional
            }).ToList()
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "Interface query test model (AB#7019)",
            CkLanguage = 2,
            Dependencies = [systemId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Name-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Rank-1"), ValueType = AttributeValueTypesDto.Integer },
                new CkAttributeDto { AttributeId = new CkAttributeId("Note-1"), ValueType = AttributeValueTypesDto.String }
            ],
            Interfaces =
            [
                InterfaceOf("Named", ("Name", false), ("Rank", true)),
                InterfaceOf("Titled", ("Name", false)),
                InterfaceOf("Lonely", ("Name", false))
            ],
            Types =
            [
                Root("Asset", "Named", "Titled"),
                Derived("Pump", "Asset"),
                Root("Sensor", "Named"),
                Derived("Gauge", "Sensor"),
                Root("Valve", "Named"),
                Root("Vault", "Named")
            ]
        };
    }
}
