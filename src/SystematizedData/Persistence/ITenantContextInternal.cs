using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.SystematizedData.Persistence;

internal interface ITenantContextInternal : ITenantContext
{
    ITenantRepositoryInternal InternalRepository { get; }
}
