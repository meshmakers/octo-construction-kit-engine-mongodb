using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests.RestoreSystemModel;

/// <summary>
///     The restore scope resolves exactly the restoring tenant id, in exactly the restoring async flow, while the scope is
///     open - never another tenant, never a parallel restore, never a flow that outlives the scope.
/// </summary>
public class RestoringTenantScopeTests
{
    [Fact]
    public void InsideTheScope_OnlyTheRestoringTenantResolvesToTheRestoredDatabase()
    {
        using (RestoringTenantScope.Begin("TenantA", "db_a"))
        {
            Assert.True(RestoringTenantScope.TryGetDatabaseName("tenanta", out var database));
            Assert.Equal("db_a", database);
            Assert.False(RestoringTenantScope.TryGetDatabaseName("tenantb", out _));
        }

        Assert.False(RestoringTenantScope.TryGetDatabaseName("tenanta", out _));
    }

    [Fact]
    public async Task ParallelRestores_SeeOnlyTheirOwnTenant()
    {
        using var bothInside = new Barrier(2);

        async Task<(bool Own, bool Other)> RestoreAsync(string own, string other)
        {
            await Task.Yield();
            using var scope = RestoringTenantScope.Begin(own, $"db_{own}");
            bothInside.SignalAndWait(TimeSpan.FromSeconds(10));
            await Task.Delay(10);
            return (RestoringTenantScope.TryGetDatabaseName(own, out var db) && db == $"db_{own}",
                RestoringTenantScope.TryGetDatabaseName(other, out _));
        }

        var results = await Task.WhenAll(Task.Run(() => RestoreAsync("a", "b")), Task.Run(() => RestoreAsync("b", "a")));

        Assert.All(results, r => Assert.True(r.Own));
        Assert.All(results, r => Assert.False(r.Other));
    }

    [Fact]
    public async Task AFlowStartedInsideTheScope_LosesItWhenTheScopeEnds()
    {
        var scopeEnded = new TaskCompletionSource();
        Task<bool> leaked;
        using (RestoringTenantScope.Begin("tenanta", "db_a"))
        {
            leaked = Task.Run(async () =>
            {
                await scopeEnded.Task;
                return RestoringTenantScope.TryGetDatabaseName("tenanta", out _);
            });
        }

        scopeEnded.SetResult();

        Assert.False(await leaked);
    }

    [Fact]
    public void ANestedScope_RestoresTheOuterOneOnDispose()
    {
        using var outer = RestoringTenantScope.Begin("tenanta", "db_a");
        using (RestoringTenantScope.Begin("tenantb", "db_b"))
        {
            Assert.False(RestoringTenantScope.TryGetDatabaseName("tenanta", out _));
            Assert.True(RestoringTenantScope.TryGetDatabaseName("tenantb", out _));
        }

        Assert.True(RestoringTenantScope.TryGetDatabaseName("tenanta", out var database));
        Assert.Equal("db_a", database);
    }
}
