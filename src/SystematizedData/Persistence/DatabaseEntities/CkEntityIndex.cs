using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkEntityIndex
{
    public IndexTypes IndexType { get; set; }

    [BsonIgnoreIfNull] public string Language { get; set; }

    public ICollection<CkIndexFields> Fields { get; set; }
}
