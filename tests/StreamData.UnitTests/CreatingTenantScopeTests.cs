namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

/// <summary>
///     AB#10060: the creating scope resolves exactly the tenant being created, in exactly the creating async flow, while
///     the scope is open - never another tenant, never a parallel creation, never a flow that outlives the scope.
/// </summary>
public class CreatingTenantScopeTests
{
    [Fact]
    public void InsideTheScope_OnlyTheCreatedTenantResolvesToItsNewDatabase()
    {
        using (CreatingTenantScope.Begin("TenantA", "db_a"))
        {
            Assert.True(CreatingTenantScope.TryGetDatabaseName("tenanta", out var database));
            Assert.Equal("db_a", database);
            Assert.False(CreatingTenantScope.TryGetDatabaseName("tenantb", out _));
        }

        Assert.False(CreatingTenantScope.TryGetDatabaseName("tenanta", out _));
    }

    [Fact]
    public async Task ParallelCreations_SeeOnlyTheirOwnTenant()
    {
        using var bothInside = new Barrier(2);

        async Task<(bool Own, bool Other)> CreateAsync(string own, string other)
        {
            await Task.Yield();
            using var scope = CreatingTenantScope.Begin(own, $"db_{own}");
            bothInside.SignalAndWait(TimeSpan.FromSeconds(10));
            await Task.Delay(10);
            return (CreatingTenantScope.TryGetDatabaseName(own, out var db) && db == $"db_{own}",
                CreatingTenantScope.TryGetDatabaseName(other, out _));
        }

        var results = await Task.WhenAll(Task.Run(() => CreateAsync("a", "b")), Task.Run(() => CreateAsync("b", "a")));

        Assert.All(results, r => Assert.True(r.Own));
        Assert.All(results, r => Assert.False(r.Other));
    }

    [Fact]
    public async Task AFlowStartedInsideTheScope_LosesItWhenTheScopeEnds()
    {
        var scopeEnded = new TaskCompletionSource();
        Task<bool> leaked;
        using (CreatingTenantScope.Begin("tenanta", "db_a"))
        {
            leaked = Task.Run(async () =>
            {
                await scopeEnded.Task;
                return CreatingTenantScope.TryGetDatabaseName("tenanta", out _);
            });
        }

        scopeEnded.SetResult();

        Assert.False(await leaked);
    }

    [Fact]
    public void ANestedScope_RestoresTheOuterOneOnDispose()
    {
        using var outer = CreatingTenantScope.Begin("tenanta", "db_a");
        using (CreatingTenantScope.Begin("tenantb", "db_b"))
        {
            Assert.False(CreatingTenantScope.TryGetDatabaseName("tenanta", out _));
            Assert.True(CreatingTenantScope.TryGetDatabaseName("tenantb", out _));
        }

        Assert.True(CreatingTenantScope.TryGetDatabaseName("tenanta", out var database));
        Assert.Equal("db_a", database);
    }
}
