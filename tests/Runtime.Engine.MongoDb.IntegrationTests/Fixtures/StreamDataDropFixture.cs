using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     System fixture with a fake <see cref="IStreamDataRepositoryFactory" /> registered, the shipped
///     System.StreamData model descriptor (so child tenants can own archive entities) and stream data
///     enabled at instance level, so the tenant drop's archive-table drop (AB#4255) can be observed
///     without CrateDB. <see cref="StreamDataDisabledDropFixture" /> is the same with the instance flag
///     off.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class StreamDataDropFixture : SystemFixture
{
    public StreamDataDropFixture() : this(instanceEnabled: true)
    {
    }

    protected StreamDataDropFixture(bool instanceEnabled)
    {
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = instanceEnabled);
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));
        Services.AddSingleton(StreamDataRepositoryFactory);
    }

    public IStreamDataRepositoryFactory StreamDataRepositoryFactory { get; } = A.Fake<IStreamDataRepositoryFactory>();
}

/// <summary>
///     <see cref="StreamDataDropFixture" /> with <c>StreamData:Enabled = false</c>: a registered factory on
///     an instance without stream data must never be asked to drop anything.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class StreamDataDisabledDropFixture : StreamDataDropFixture
{
    public StreamDataDisabledDropFixture() : base(instanceEnabled: false)
    {
    }
}
