using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkIndexFields
{
    [BsonIgnoreIfNull] public int? Weight { get; set; }

    public ICollection<string> AttributeNames { get; set; }
}
