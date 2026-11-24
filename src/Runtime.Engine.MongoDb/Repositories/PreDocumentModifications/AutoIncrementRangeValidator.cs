using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     AB#9754: refuses a <c>System/AutoIncrement</c> number range whose configuration is not valid when it is written,
///     before anything is stored - above all a fiscal-year range (<c>PeriodKind = FiscalYear</c>) without a known IANA
///     <c>ReferenceTimeZone</c>, which would otherwise be read in some default zone. The rules are
///     <see cref="AutoIncrementRangeSettings.TryCreate" />.
///     <list type="bullet">
///         <item>Insert, replace and bulk import (<see cref="IPreDocumentModification{TDocument}" />): the written
///         entity is checked.</item>
///         <item>Partial update (<see cref="IPreDocumentUpdateValidation{TDocument}" />, AB#9755): the stored counter
///         with the attributes of the update laid over it is checked, so an update that makes the range invalid (e.g.
///         <c>PeriodKind = FiscalYear</c> without a zone, a <c>StartValue</c> above <c>End</c>) is refused up front.
///         Only updates that set a configuration attribute read the stored counter; an update of the counter alone
///         reads nothing.</item>
///     </list>
///     The allocation checks the same rules again before it hands out a number, so a range that reaches the database
///     by another path (a direct collection update, a write with the pre-document modifications disabled, or two
///     partial updates that are each valid on their own but not together) never numbers in a wrong year either.
/// </summary>
public class AutoIncrementRangeValidator(ICkCacheService ckCacheService)
    : IPreDocumentModification<RtEntity>, IPreDocumentUpdateValidation<RtEntity>
{
    private static readonly RtCkId<CkTypeId> AutoIncrementTypeId = SystemCkIds.CkAutoIncrementTypeId.ToRtCkId();

    /// <summary>The attributes that make up the range configuration (a partial update of others is not checked).</summary>
    private static readonly HashSet<string> ConfigurationAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        AutoIncrementRangeSettings.FormatAttribute,
        AutoIncrementRangeSettings.StartValueAttribute,
        AutoIncrementRangeSettings.EndAttribute,
        AutoIncrementRangeSettings.PeriodKindAttribute,
        AutoIncrementRangeSettings.FiscalYearStartMonthAttribute,
        AutoIncrementRangeSettings.ReferenceTimeZoneAttribute
    };

    public Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        foreach (var document in documents)
        {
            if (IsAutoIncrement(document))
            {
                Validate(document, document);
            }
        }

        return Task.CompletedTask;
    }

    public async Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IReadOnlyList<RtEntity> partialDocuments)
    {
        var updates = partialDocuments
            .Where(d => IsAutoIncrement(d) && d.Attributes.Keys.Any(ConfigurationAttributes.Contains))
            .ToList();
        if (updates.Count == 0)
        {
            return;
        }

        // Version-less, as the AutoIncrementModifier looks the type up (D-G1-1).
        var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, AutoIncrementTypeId) ??
                          throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId,
                              AutoIncrementTypeId);
        var stored = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource, ckTypeGraph, updates);

        foreach (var update in updates)
        {
            if (!stored.TryGetValue(update.RtId, out var counter))
            {
                // Nothing stored: the update matches no document and writes nothing.
                continue;
            }

            foreach (var (attributeName, value) in update.Attributes)
            {
                counter.SetAttributeRawValue(attributeName, value);
            }

            Validate(counter, update);
        }
    }

    private static void Validate(RtEntity range, RtEntity written)
    {
        if (!AutoIncrementRangeSettings.TryCreate(range, out _, out var error))
        {
            throw AutoIncrementFailedException.InvalidNumberRange(written.RtId,
                written.RtWellKnownName ?? range.RtWellKnownName, error!);
        }
    }

    private static bool IsAutoIncrement(RtEntity document)
    {
        var ckTypeId = document.CkTypeId ?? document.GetRtCkTypeId();
        // Version-less, as the AutoIncrementModifier looks the type up (D-G1-1).
        return string.Equals(ckTypeId.FullName, AutoIncrementTypeId.FullName, StringComparison.Ordinal);
    }
}
