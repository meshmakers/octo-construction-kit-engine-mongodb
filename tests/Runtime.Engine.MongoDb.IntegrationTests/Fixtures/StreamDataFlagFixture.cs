using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     System fixture with stream data enabled at instance level and the shipped System.StreamData
///     model descriptor registered, so <c>EnableStreamDataAsync</c> can flip the tenant flag and import
///     the model. Deliberately registers no <c>IStreamDataRepositoryFactory</c>: the tests here cover
///     the Mongo-side flag and archive-status logic (AB#4255) and need no CrateDB.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class StreamDataFlagFixture : SystemFixture
{
    public StreamDataFlagFixture()
    {
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));
    }
}
