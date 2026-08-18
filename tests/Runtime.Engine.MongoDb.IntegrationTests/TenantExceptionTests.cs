using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Contract tests for the <see cref="TenantException"/> discriminators. Consumers such as the durable
/// setup-retry loop classify failures by these flags instead of parsing message text, so a factory that
/// stops setting its flag silently turns a terminal condition back into an endlessly retried one
/// (AB#4829).
/// </summary>
public class TenantExceptionTests
{
    [Fact]
    public void TenantDoesNotExist_IsMarkedAsTenantNotFound()
    {
        // The retry loop drops a pending setup entry when the tenant is gone from the registry —
        // retrying can never drive it to completion, and each retried setup used to re-create the
        // just-deleted tenant's database as an empty shell (AB#4829).
        var exception = Assert.IsType<TenantException>(TenantException.TenantDoesNotExist("t-gone"));

        Assert.True(exception.IsTenantNotFound);
        Assert.False(exception.IsConflict);
    }

    [Fact]
    public void ConflictAndOtherTenantExceptions_AreNotMarkedAsTenantNotFound()
    {
        // Only the registry miss is terminal for a retry. Conflicts and infrastructure failures must
        // keep their retry semantics.
        Assert.False(Assert.IsType<TenantException>(TenantException.TenantIdNotAvailable("t")).IsTenantNotFound);
        Assert.False(Assert.IsType<TenantException>(TenantException.DatabaseNameNotAvailable("db")).IsTenantNotFound);
        Assert.False(Assert.IsType<TenantException>(TenantException.TenantDatabaseDoesNotExist("db")).IsTenantNotFound);
        Assert.False(Assert.IsType<TenantException>(TenantException.SystemTenantDatabaseNotExisting()).IsTenantNotFound);
    }
}
