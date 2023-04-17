using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class AutoCompleteText
{
    [BsonElement("count")] public int OccurrenceCount { get; set; }

    [BsonElement("_id")] public string Text { get; set; }
}
