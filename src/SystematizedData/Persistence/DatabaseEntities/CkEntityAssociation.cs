using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

[CollectionName("CkEntityAssociations")]
[DebuggerDisplay("{" + nameof(RoleId) + "} -> {" + nameof(TargetCkId) + "}")]
public class CkEntityAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId AssociationId { get; set; }

    [BsonRequired] public ScopeIds ScopeId { get; set; }

    [BsonRequired] public string OriginCkId { get; set; }

    [BsonRequired] public string TargetCkId { get; set; }

    /// <summary>
    ///     Name of the association for inbound references (e. g. Parent)
    /// </summary>
    [BsonRequired]
    public string InboundName { get; set; }

    /// <summary>
    ///     Name of the association for outbound references (e. g. Children)
    /// </summary>
    [BsonRequired]
    public string OutboundName { get; set; }

    /// <summary>
    ///     Multiplicity of the inbound association
    /// </summary>
    [BsonRequired]
    public Multiplicities InboundMultiplicity { get; set; }

    /// <summary>
    ///     Multiplicity of the outbound association
    /// </summary>
    [BsonRequired]
    public Multiplicities OutboundMultiplicity { get; set; }

    [BsonRequired] public string RoleId { get; set; }
}
