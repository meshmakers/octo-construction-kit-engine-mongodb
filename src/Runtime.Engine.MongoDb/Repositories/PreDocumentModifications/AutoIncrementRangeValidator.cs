using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
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
///     <c>ReferenceTimeZone</c>, which would otherwise be read in some default zone, or (AB#9755) a legacy
///     <c>string.Format</c> format that cannot format a number. The rules are
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

    public async Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        var ranges = documents.Where(IsAutoIncrement).ToList();
        foreach (var range in ranges)
        {
            Validate(range, range);
        }

        // A legacy format without the counter is refused when it is configured: on an insert, and on a replace that
        // changes the format. A stored range keeps its format on a replace (e.g. a seed re-apply) and keeps numbering.
        // Only such formats cause the read, so every other write reads nothing.
        var withoutCounter = ranges.Where(r => !ShowsTheCounter(r)).ToList();
        if (withoutCounter.Count == 0)
        {
            return;
        }

        var stored = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource,
            GetAutoIncrementTypeGraph(repositoryDataSource), withoutCounter);
        foreach (var range in withoutCounter)
        {
            if (!stored.TryGetValue(range.RtId, out var storedRange) || FormatOf(storedRange) != FormatOf(range))
            {
                RefuseWithoutCounter(range, range);
            }
        }
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

        var stored = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource,
            GetAutoIncrementTypeGraph(repositoryDataSource), updates);

        foreach (var update in updates)
        {
            if (!stored.TryGetValue(update.RtId, out var counter))
            {
                // Nothing stored: the update matches no document and writes nothing.
                continue;
            }

            var formatBefore = FormatOf(counter);
            foreach (var (attributeName, value) in update.Attributes)
            {
                counter.SetAttributeRawValue(attributeName, value);
            }

            Validate(counter, update);
            if (FormatOf(counter) != formatBefore && !ShowsTheCounter(counter))
            {
                RefuseWithoutCounter(counter, update);
            }
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

    private static string? FormatOf(RtEntity range) =>
        range.GetAttributeValueOrDefault(AutoIncrementRangeSettings.FormatAttribute) as string;

    /// <summary>
    ///     True unless the range has a legacy format whose text does not depend on the number. Called after
    ///     <see cref="Validate" />, so the format parses.
    /// </summary>
    private static bool ShowsTheCounter(RtEntity range) =>
        !AutoIncrementNumberFormat.TryParse(FormatOf(range), out var format, out _) || format.ShowsTheCounter;

    private static void RefuseWithoutCounter(RtEntity range, RtEntity written) =>
        throw AutoIncrementFailedException.InvalidNumberRange(written.RtId,
            written.RtWellKnownName ?? range.RtWellKnownName,
            $"Format '{FormatOf(range)}' does not show the counter ({{0}}), so every number would be the same text; " +
            "use {0} or {0:D<n>} for the counter, or a token format such as {counter:D5}.");

    // Version-less, as the AutoIncrementModifier looks the type up (D-G1-1).
    private CkTypeGraph GetAutoIncrementTypeGraph(IRepositoryDataSource repositoryDataSource) =>
        ckCacheService.GetRtCkType(repositoryDataSource.TenantId, AutoIncrementTypeId) ??
        throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId, AutoIncrementTypeId);

    private static bool IsAutoIncrement(RtEntity document)
    {
        var ckTypeId = document.CkTypeId ?? document.GetRtCkTypeId();
        // Version-less, as the AutoIncrementModifier looks the type up (D-G1-1).
        return string.Equals(ckTypeId.FullName, AutoIncrementTypeId.FullName, StringComparison.Ordinal);
    }
}
