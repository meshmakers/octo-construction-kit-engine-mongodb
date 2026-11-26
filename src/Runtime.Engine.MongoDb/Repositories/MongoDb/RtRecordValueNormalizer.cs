using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Turns record values of an attribute into the shape the object serializer of the update path accepts: a plain
///     <see cref="RtRecord" /> (or a <see cref="List{RtRecord}" />), never a generated subclass such as
///     <c>RtContactNameRecord</c>.
/// </summary>
/// <remarks>
///     An attribute update is written as <c>$set: { "attributes.x": value }</c> with the value boxed as
///     <see cref="object" />, and the registered object serializer allows only the runtime contract types
///     (<c>MongoRepositoryClient.OctoObjectAllowedTypes</c>). A generated record class of any CK model lives in its own
///     namespace, so updating an entity with a typed record failed with "Type … is not configured as a type that is
///     allowed to be serialized", while inserting the same entity stored it as a plain record. The stored document is
///     the same either way: the CK record id and the attributes.
/// </remarks>
internal static class RtRecordValueNormalizer
{
    /// <summary>The value unchanged when it holds no generated record subclass, otherwise a plain copy.</summary>
    public static object? ToStorable(object? value)
    {
        return NeedsCopy(value) ? Copy(value) : value;
    }

    private static bool NeedsCopy(object? value) => value switch
    {
        null or string or byte[] => false,
        RtRecord record => record.GetType() != typeof(RtRecord) || record.Attributes.Values.Any(NeedsCopy),
        IEnumerable<RtRecord> records => records.GetType() != typeof(List<RtRecord>) || records.Any(NeedsCopy),
        _ => false
    };

    private static object? Copy(object? value) => value switch
    {
        RtRecord record => new RtRecord(record.CkRecordId,
            record.Attributes.ToDictionary(a => a.Key, a => ToStorable(a.Value), StringComparer.Ordinal)),
        IEnumerable<RtRecord> records => records.Select(r => (RtRecord)Copy(r)!).ToList(),
        _ => value
    };
}
