using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[CollectionName("CkEntityInheritances")]
public class CkEntityInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId InheritanceId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public string OriginCkId { get; set; }

    public string TargetCkId { get; set; }
}
