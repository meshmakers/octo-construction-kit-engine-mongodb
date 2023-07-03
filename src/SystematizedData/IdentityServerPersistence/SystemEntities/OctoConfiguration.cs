using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

[CollectionName("OctoConfigurations")]
public class OctoConfiguration
{
    [BsonId(IdGenerator = typeof(NullIdChecker))]
    public string Key { get; set; }

    public object Value { get; set; }
}
