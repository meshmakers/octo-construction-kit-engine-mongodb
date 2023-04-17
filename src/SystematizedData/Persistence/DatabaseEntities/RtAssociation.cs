using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

[CollectionName("RtAssociations")]
public class RtAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId AssociationId { get; set; }

    [BsonRequired] public ObjectId OriginRtId { get; set; }

    [BsonRequired] public string OriginCkId { get; set; }

    [BsonRequired] public ObjectId TargetRtId { get; set; }

    [BsonRequired] public string TargetCkId { get; set; }

    [BsonRequired] public string AssociationRoleId { get; set; }
}
