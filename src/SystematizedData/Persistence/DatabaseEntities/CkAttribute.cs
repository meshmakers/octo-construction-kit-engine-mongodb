using System.Collections.Generic;
using System.Diagnostics;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using Newtonsoft.Json;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[CollectionName("CkAttributes")]
[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkAttribute
{
    [BsonId(IdGenerator = typeof(NullIdChecker))]
    public string AttributeId { get; set; }

    [BsonRequired] public ScopeIds ScopeId { get; set; }

    [BsonRequired] public AttributeValueTypes AttributeValueType { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object DefaultValue { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ICollection<object> DefaultValues { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ICollection<CkSelectionValue> SelectionValues { get; set; }
}
