using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

namespace Persistence.InternalContracts;

public record TenantDatabaseSourceIdentifier(ICkDatabaseContext DatabaseContext, IOctoSession Session);