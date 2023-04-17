using Meshmakers.Octo.Backend.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.Persistence;

internal interface ITenantContextInternal : ITenantContext
{
    ITenantRepositoryInternal InternalRepository { get; }
}
