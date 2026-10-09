using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 Phase 0 kitchen-sink model built in C# (contract §3.4): every new construct with every field set.
///     <list type="bullet">
///         <item><c>ckLanguage: 2</c></item>
///         <item>Interface <c>Named-1</c> (required <c>Name</c>, optional <c>Alias</c>) and <c>Coded-1</c> (<c>Code</c>)</item>
///         <item>
///             Types: abstract <c>Thing</c> implements Named; <c>Gadget</c> derives from Thing (inherits Named)
///             and implements Coded; unrelated <c>Widget</c> implements Named
///         </item>
///         <item>All four <c>access</c> values on type, record and association-role assignments</item>
///         <item>Method <c>ChangePassword-2</c> with every field set and the minimal <c>Ping-1</c></item>
///     </list>
/// </summary>
internal static class CkV2KitchenSinkModel
{
    internal static readonly CkModelId ModelId = new("KitchenSinkCs-1.0.0");

    internal static CkCompiledModelRoot Build(CkModelId systemId, CkModelId? modelId = null)
    {
        var id = modelId ?? ModelId;
        CkId<CkAttributeId> Attribute(string name) => new(id, new CkAttributeId($"{name}-1"));
        CkId<CkInterfaceId> Interface(string name) => new(id, new CkInterfaceId($"{name}-1"));

        CkTypeAttributeDto Assign(string name, CkAttributeAccessDto? access, bool isOptional = true) => new()
        {
            CkAttributeId = Attribute(name), AttributeName = name, IsOptional = isOptional, Access = access
        };

        var method = RoundTripMethods.Full(id);

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "CK v2 Phase 0 kitchen sink",
            CkLanguage = 2,
            Dependencies = [systemId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Name-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Alias-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("Secret-1"), ValueType = AttributeValueTypesDto.String,
                    Ownership = AttributeOwnershipDto.Secret
                },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("Mode-1"), ValueType = AttributeValueTypesDto.Enum,
                    ValueCkEnumId = new CkId<CkEnumId>(id, new CkEnumId("Mode-1"))
                }
            ],
            Enums =
            [
                new CkEnumDto
                {
                    EnumId = new CkEnumId("Mode-1"),
                    Values =
                    [
                        new CkEnumValueDto { Key = 0, Name = "Fast" },
                        new CkEnumValueDto { Key = 1, Name = "Safe" }
                    ]
                }
            ],
            Records =
            [
                new CkRecordDto
                {
                    RecordId = new CkRecordId("Address-1"),
                    Attributes =
                    [
                        Assign("Name", null),
                        Assign("Alias", CkAttributeAccessDto.ReadOnly),
                        Assign("Code", CkAttributeAccessDto.MethodOnly),
                        Assign("Secret", CkAttributeAccessDto.Hidden)
                    ]
                }
            ],
            Interfaces =
            [
                new CkInterfaceDto
                {
                    InterfaceId = new CkInterfaceId("Named-1"),
                    Description = "things with a name",
                    Attributes =
                    [
                        new CkInterfaceAttributeDto { CkAttributeId = Attribute("Name"), AttributeName = "Name" },
                        new CkInterfaceAttributeDto
                        {
                            CkAttributeId = Attribute("Alias"), AttributeName = "Alias", IsOptional = true
                        }
                    ]
                },
                new CkInterfaceDto
                {
                    InterfaceId = new CkInterfaceId("Coded-1"),
                    Attributes = [new CkInterfaceAttributeDto { CkAttributeId = Attribute("Code"), AttributeName = "Code" }]
                }
            ],
            AssociationRoles =
            [
                new CkAssociationRoleDto
                {
                    AssociationRoleId = new CkAssociationRoleId("Link-1"),
                    InboundName = "LinkedFrom",
                    OutboundName = "LinksTo",
                    InboundMultiplicity = MultiplicitiesDto.N,
                    OutboundMultiplicity = MultiplicitiesDto.N,
                    Attributes =
                    [
                        Assign("Alias", CkAttributeAccessDto.ReadWrite),
                        Assign("Code", CkAttributeAccessDto.ReadOnly),
                        Assign("Mode", CkAttributeAccessDto.MethodOnly),
                        // Hidden is not allowed on association roles (engine rule 108, Phase 1).
                        Assign("Secret", CkAttributeAccessDto.ReadOnly)
                    ]
                }
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Thing-1"),
                    IsAbstract = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                    Implements = [Interface("Named")],
                    Attributes =
                    [
                        Assign("Name", CkAttributeAccessDto.ReadWrite, isOptional: false),
                        Assign("Alias", CkAttributeAccessDto.ReadOnly),
                        Assign("Secret", CkAttributeAccessDto.Hidden)
                    ],
                    Methods = [method, RoundTripMethods.Static(), new CkMethodDto { MethodId = "Ping-1" }]
                },
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Gadget-1"),
                    IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(id, new CkTypeId("Thing-1")),
                    Implements = [Interface("Coded")],
                    // Required interface member (Coded.Code) → required on the implementing type (rule 97).
                    Attributes = [Assign("Code", CkAttributeAccessDto.MethodOnly, isOptional: false)],
                    Associations =
                    [
                        new CkTypeAssociationDto
                        {
                            CkRoleId = new CkId<CkAssociationRoleId>(id, new CkAssociationRoleId("Link-1")),
                            TargetCkTypeId = new CkId<CkTypeId>(id, new CkTypeId("Widget-1"))
                        }
                    ]
                },
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Widget-1"),
                    IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                    Implements = [Interface("Named")],
                    Attributes =
                    [
                        Assign("Name", null, isOptional: false),
                        Assign("Mode", CkAttributeAccessDto.ReadOnly)
                    ]
                }
            ]
        };
    }
}

/// <summary>
///     Shared method fixtures of the CK v2 persistence tests.
/// </summary>
internal static class RoundTripMethods
{
    /// <summary>A static method (the only non-default <see cref="CkMethodKindDto" />).</summary>
    internal static CkMethodDto Static() => new()
    {
        MethodId = "Reindex-1", Kind = CkMethodKindDto.Static, Authorization = new CkMethodAuthorizationDto
        {
            Roles = ["UserManagement"]
        }
    };

    /// <summary>A method with every field of <see cref="CkMethodDto" /> and its parts set.</summary>
    internal static CkMethodDto Full(CkModelId modelId) => new()
    {
        MethodId = "ChangePassword-2",
        // Instance (the default): allowSelf requires a target entity (rule 104). Kind is covered by Static().
        Description = "changes the password",
        Parameters =
        [
            new CkMethodParameterDto
            {
                Name = "newPassword", ValueType = AttributeValueTypesDto.String, IsOptional = true, Sensitive = true,
                Description = "the new one"
            },
            new CkMethodParameterDto
            {
                Name = "address", ValueType = AttributeValueTypesDto.Record,
                ValueCkRecordId = new CkId<CkRecordId>(modelId, new CkRecordId("Address-1"))
            },
            new CkMethodParameterDto
            {
                Name = "mode", ValueType = AttributeValueTypesDto.Enum,
                ValueCkEnumId = new CkId<CkEnumId>(modelId, new CkEnumId("Mode-1"))
            }
        ],
        Result = new CkMethodResultDto
        {
            ValueType = AttributeValueTypesDto.Enum,
            ValueCkEnumId = new CkId<CkEnumId>(modelId, new CkEnumId("Mode-1"))
        },
        Errors = [new CkMethodErrorDto { Code = "PASSWORD_POLICY_VIOLATION", Description = "too weak" }],
        Authorization = new CkMethodAuthorizationDto
        {
            Roles = ["UserManagement"], AllowSelf = true, Scopes = ["octo_api", "extra"]
        },
        Execution = new CkMethodExecutionDto { TimeoutSeconds = 30, Idempotent = true }
    };
}
