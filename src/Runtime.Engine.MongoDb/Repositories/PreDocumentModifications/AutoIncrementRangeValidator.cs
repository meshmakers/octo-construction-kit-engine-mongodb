using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     AB#9754: refuses a <c>System/AutoIncrement</c> number range whose configuration is not valid when it is written
///     (insert, replace, bulk import), before anything is stored - above all a fiscal-year range
///     (<c>PeriodKind = FiscalYear</c>) without a known IANA <c>ReferenceTimeZone</c>, which would otherwise be read in
///     some default zone. The rules are <see cref="AutoIncrementRangeSettings.TryCreate" />. The allocation checks
///     the same rules again before it hands out a number, so a range that reaches the database by another path
///     (a partial update, a write without pre-document modifications) never numbers in a wrong year either.
/// </summary>
public class AutoIncrementRangeValidator : IPreDocumentModification<RtEntity>
{
    private static readonly RtCkId<CkTypeId> AutoIncrementTypeId = SystemCkIds.CkAutoIncrementTypeId.ToRtCkId();

    public Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        foreach (var document in documents)
        {
            if (!IsAutoIncrement(document))
            {
                continue;
            }

            if (!AutoIncrementRangeSettings.TryCreate(document, out _, out var error))
            {
                throw AutoIncrementFailedException.InvalidNumberRange(document.RtId, document.RtWellKnownName, error!);
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsAutoIncrement(RtEntity document)
    {
        var ckTypeId = document.CkTypeId ?? document.GetRtCkTypeId();
        // Version-less, as the AutoIncrementModifier looks the type up (D-G1-1).
        return string.Equals(ckTypeId.FullName, AutoIncrementTypeId.FullName, StringComparison.Ordinal);
    }
}
