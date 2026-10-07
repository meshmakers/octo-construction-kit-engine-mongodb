using System.Security.Cryptography;

using Meshmakers.Octo.Runtime.Engine.Secrets;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     <see cref="ImportTestCkModelFixture" /> with a configured key ring (one generated key), so the secret sweep
///     (<c>ISecretMaintenanceService</c>) can encrypt against the real MongoDB repository (AB#5533).
/// </summary>
public class SecretSweepFixture : ImportTestCkModelFixture
{
    public const string KeyId = "k1";

    public SecretSweepFixture()
    {
        Services.Configure<SecretEncryptionOptions>(options =>
        {
            options.Keys[KeyId] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            options.ActiveKeyId = KeyId;
        });
    }
}
