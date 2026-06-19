namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Public façade for opening a per-request MongoDB statistics scope. Used by HTTP middleware
/// and GraphQL execution listeners that live in upstream packages (octo-common-services,
/// octo-asset-repo-services) — those packages must not reference the internal
/// <c>MongoCommandObservability</c> listener directly.
/// </summary>
/// <remarks>
/// Inside a scope, every MongoDB command issued on the same async flow is summed into the
/// returned <see cref="RequestMongoStats"/>. The data is then surfaced back to the caller as
/// either a GraphQL <c>extensions.mongoDb</c> block or as REST response headers, so the user
/// who triggered the request sees how much MongoDB cost was incurred.
/// </remarks>
public static class MongoRequestScope
{
    /// <summary>
    /// Opens a per-request scope; dispose the returned handle to close it. Typical usage is
    /// inside an ASP.NET Core middleware: <c>using var _ = MongoRequestScope.Begin(out var stats);</c>
    /// </summary>
    public static IDisposable Begin(out RequestMongoStats stats)
        => MongoCommandObservability.BeginRequestScope(out stats);

    /// <summary>
    /// Returns the <see cref="RequestMongoStats"/> for the currently active scope on this
    /// async flow, or <c>null</c> if no scope is open. Used by consumers that did not open
    /// the scope themselves but need to read what was accumulated — e.g. a GraphQL document
    /// execution listener inside a request whose scope was opened by upstream middleware.
    /// </summary>
    public static RequestMongoStats? Current
        => MongoCommandObservability.GetCurrentScope();
}
