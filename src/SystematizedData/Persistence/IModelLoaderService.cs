using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface IModelLoaderService
{
    Task LoadAsync(string tenantId, IOctoSession session, IDatabaseContext databaseContext);
}