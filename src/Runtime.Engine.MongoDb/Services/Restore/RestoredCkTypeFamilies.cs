using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     The CK type inheritance of a restored tenant database, read from its inheritance rows (the same rows the engine
///     builds its CK cache from), with every type id as <c>Model/Type</c>.
/// </summary>
internal sealed class RestoredCkTypeFamilies
{
    private const string InheritanceCollectionName = nameof(CkTypeInheritance);
    private const string BaseTypeField = "baseCkTypeId";
    private const string InheritorTypeField = "inheritorCkTypeId";

    private readonly Dictionary<string, HashSet<string>> _directInheritors;

    private RestoredCkTypeFamilies(Dictionary<string, HashSet<string>> directInheritors)
    {
        _directInheritors = directInheritors;
    }

    /// <summary>Reads the inheritance rows of <paramref name="database" />.</summary>
    public static async Task<RestoredCkTypeFamilies> ReadAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var inheritanceRows = await database.GetCollection<BsonDocument>(InheritanceCollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project(Builders<BsonDocument>.Projection.Include(BaseTypeField).Include(InheritorTypeField))
            .ToListAsync(cancellationToken);

        var directInheritors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var inheritanceRow in inheritanceRows)
        {
            if (TryReadTypeId(inheritanceRow, BaseTypeField, out var baseTypeId) &&
                TryReadTypeId(inheritanceRow, InheritorTypeField, out var inheritorTypeId))
            {
                if (!directInheritors.TryGetValue(baseTypeId, out var inheritors))
                {
                    inheritors = new HashSet<string>(StringComparer.Ordinal);
                    directInheritors[baseTypeId] = inheritors;
                }

                inheritors.Add(inheritorTypeId);
            }
        }

        return new RestoredCkTypeFamilies(directInheritors);
    }

    /// <summary><paramref name="ckTypeId" /> and every type derived from it, directly or indirectly.</summary>
    public IReadOnlySet<string> WithDerivedTypes(string ckTypeId)
    {
        var family = new HashSet<string>(StringComparer.Ordinal) { ckTypeId };
        var pending = new Queue<string>([ckTypeId]);
        while (pending.TryDequeue(out var currentTypeId))
        {
            if (!_directInheritors.TryGetValue(currentTypeId, out var inheritors))
            {
                continue;
            }

            foreach (var inheritorTypeId in inheritors.Where(family.Add))
            {
                pending.Enqueue(inheritorTypeId);
            }
        }

        return family;
    }

    private static bool TryReadTypeId(BsonDocument inheritanceRow, string fieldName, out string runtimeTypeId)
    {
        runtimeTypeId = string.Empty;
        if (!inheritanceRow.TryGetValue(fieldName, out var storedValue) || !storedValue.IsString)
        {
            return false;
        }

        try
        {
            runtimeTypeId = new CkId<CkTypeId>(storedValue.AsString).ToRtCkId().ToString();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }
}
