using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

namespace Persistence.InternalContracts;

public record TenantDatabaseSourceIdentifier(ICkDatabaseContext DatabaseContext, IOctoSession Session);