using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Bounded, reported, best-effort disposal of a child tenant a test created (AB#5436).
///     <para>
///         Tests that create a throwaway tenant used to drop it only on the happy path, so a failing
///         test left it behind — 24 failures in build 48277 left five <c>streamdrop*</c> tenants and
///         their databases in the shared MongoDB container with nothing in the log to say so. Cleaning
///         up in a <c>finally</c> fixes the leak, but a cleanup in a <c>finally</c> is also exactly the
///         place where a run can get stuck for good: it runs after the failure, against a tenant that
///         is in an unknown state, and anything it waits for it waits for forever.
///     </para>
///     <para>
///         Hence: one time budget, one reported line per attempt, and no exception ever leaves here.
///         The failure the test already reported stays the run's verdict; the run still goes red, it
///         just does not stop.
///     </para>
/// </summary>
internal static class ThrowawayTenant
{
    private static readonly TimeSpan DropTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Drops <paramref name="tenantId" /> if it is still there. Safe to call when the test under
    ///     way already dropped it — that is the normal case, and then this only costs one existence
    ///     check.
    /// </summary>
    internal static Task DropAsync(ISystemContext systemContext, string tenantId, bool dropStreamData = true)
    {
        return RunProgress.RunBoundedAsync($"clean up throwaway tenant '{tenantId}'", async () =>
        {
            bool existing;
            using (var probe = await systemContext.GetAdminSessionAsync())
            {
                probe.StartTransaction();
                existing = await systemContext.IsChildTenantExistingAsync(probe, tenantId);
                await probe.CommitTransactionAsync();
            }

            if (!existing)
            {
                return;
            }

            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId, dropStreamData);
            await session.CommitTransactionAsync();
        }, DropTimeout);
    }
}
