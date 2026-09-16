using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     A test-controlled <see cref="ITenantLocationSource" /> (AB#4924): inert until a test arms it,
///     and disarmed again when the returned handle is disposed.
/// </summary>
/// <remarks>
///     🔴 <b>Inert by default is what makes registering it in the shared fixture safe.</b> The engine
///     asks for this seam on every tenant resolve; a source that answered would send every test in the
///     suite down the detached path and the registry route would stop being exercised at all. While
///     nothing is armed, <see cref="TryGetDatabaseName" /> returns <c>false</c> and the resolve is
///     byte-for-byte the one that ran before this interface existed.
/// </remarks>
public sealed class TestTenantLocationSource : ITenantLocationSource
{
    private volatile Armed? _armed;

    /// <inheritdoc />
    public bool TryGetDatabaseName(string tenantId, out string databaseName)
    {
        var armed = _armed;

        if (armed is null || !string.Equals(armed.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            databaseName = string.Empty;
            return false;
        }

        databaseName = armed.DatabaseName;
        return true;
    }

    /// <summary>
    ///     Points this source at one tenant for the lifetime of the returned handle.
    /// </summary>
    public IDisposable Arm(string tenantId, string databaseName)
    {
        _armed = new Armed(tenantId, databaseName);
        return new Disarm(this);
    }

    private sealed record Armed(string TenantId, string DatabaseName);

    private sealed class Disarm(TestTenantLocationSource source) : IDisposable
    {
        public void Dispose() => source._armed = null;
    }
}
