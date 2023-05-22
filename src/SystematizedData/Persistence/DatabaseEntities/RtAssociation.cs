using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

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

    [BsonRequired] public string OriginCkId { get; set; } = null!;

    [BsonRequired] public ObjectId TargetRtId { get; set; }

    [BsonRequired] public string TargetCkId { get; set; }= null!;

    [BsonRequired] public string AssociationRoleId { get; set; }= null!;
}
