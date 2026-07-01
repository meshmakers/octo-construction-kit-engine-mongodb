using System;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Npgsql;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

/// <summary>
/// AB#4278: the CrateDB data source must honour <see cref="StreamDataConfiguration.PoolingEnabled"/>.
/// When pooling is on it sets <c>No Reset On Close</c> (suppresses the DISCARD ALL/reset CrateDB
/// rejects — the actual cause of the original unpooled workaround) plus the pool tunables so idle /
/// broken connections are pruned and healthy ones reused across a long multi-chunk backfill. When
/// pooling is off it reproduces the pre-AB#4278 unpooled shape.
/// </summary>
public class CrateDbConnectionStringPoolingTests
{
    private static StreamDataConfiguration NewConfig() => new()
    {
        ConnectionString = "Host=crate.test;Username=crate;SSL Mode=Prefer",
        MaxPoolSize = 40,
        MinPoolSize = 2,
        ConnectionIdleLifetime = TimeSpan.FromMinutes(5),
        ConnectionPruningInterval = TimeSpan.FromSeconds(10),
        Keepalive = TimeSpan.FromSeconds(30),
        PoolingEnabled = true,
    };

    [Fact]
    public void PoolingEnabled_SetsNoResetOnClose_AndPoolTunables()
    {
        var config = NewConfig();

        var csb = new NpgsqlConnectionStringBuilder(CrateDbConnectionAccess.BuildConnectionString(config));

        Assert.True(csb.Pooling);
        // The key fix: no session reset on return, so CrateDB never sees the DISCARD ALL it rejects.
        Assert.True(csb.NoResetOnClose);
        Assert.Equal(40, csb.MaxPoolSize);
        Assert.Equal(2, csb.MinPoolSize);
        Assert.Equal(300, csb.ConnectionIdleLifetime);   // 5 min in seconds
        Assert.Equal(10, csb.ConnectionPruningInterval);
        Assert.Equal(30, csb.KeepAlive);                 // 0 = disabled; positive = probe interval
        // The base connection string is preserved.
        Assert.Equal("crate.test", csb.Host);
        Assert.Equal("crate", csb.Username);
    }

    [Fact]
    public void PoolingDisabled_ReproducesUnpooledShape_ButKeepsNoResetOnClose()
    {
        var config = NewConfig();
        config.PoolingEnabled = false;

        var csb = new NpgsqlConnectionStringBuilder(CrateDbConnectionAccess.BuildConnectionString(config));

        Assert.False(csb.Pooling);
        // Belt-and-braces: reset stays suppressed even on the unpooled escape hatch.
        Assert.True(csb.NoResetOnClose);
        // Idle lifetime still flows through; pool-size / keepalive tuning is irrelevant when unpooled
        // and must not have been forced onto the string (they stay at Npgsql defaults).
        Assert.Equal(300, csb.ConnectionIdleLifetime);
        Assert.Equal(0, csb.KeepAlive);
    }

    [Fact]
    public void ConnectionIdleLifetime_IsFlooredToAtLeastOneSecond()
    {
        var config = NewConfig();
        config.ConnectionIdleLifetime = TimeSpan.Zero;

        var csb = new NpgsqlConnectionStringBuilder(CrateDbConnectionAccess.BuildConnectionString(config));

        Assert.True(csb.ConnectionIdleLifetime >= 1);
    }
}
