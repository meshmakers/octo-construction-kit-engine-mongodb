using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     The S3 store of the local kind infrastructure (SeaweedFS in <c>octo-infra</c>, AB#6967 / octo-tools) as the
///     tests see it: the credential read from the Secret <c>seaweedfs-s3</c> by <c>kubectl</c> (context
///     <c>kind-kind</c> only), the endpoint CrateDB uses inside the cluster, and a minimal signed client to list and
///     delete the objects a test created through the host port.
/// </summary>
/// <remarks>
///     The credential lives in two fields of this object and nowhere else: it is never written to a file, a log, a
///     message or an exception text. When the store, the Secret or <c>kubectl</c> is not there, <see cref="Available" />
///     is false and the tests that need it are skipped.
/// </remarks>
public sealed class LocalS3Store
{
    private const string KubeContext = "kind-kind";
    private const string Region = "us-east-1";

    private readonly string? _accessKey;
    private readonly string? _secretKey;

    private LocalS3Store(string? accessKey, string? secretKey, string reason)
    {
        _accessKey = accessKey;
        _secretKey = secretKey;
        Reason = reason;
    }

    public bool Available => _accessKey is not null && _secretKey is not null;

    /// <summary>Why the store is not available (never contains the credential).</summary>
    public string Reason { get; }

    public string Bucket => "octo-artifacts";

    /// <summary>The endpoint of the store from the host: where the tests list and delete objects.</summary>
    public Uri HostEndpoint => new("http://localhost:8333");

    /// <summary>The endpoint CrateDB uses inside the cluster (host and port in an <c>s3://</c> URI).</summary>
    public string CrateEndpoint => "seaweedfs.octo-infra:8333";

    /// <summary>A store that is not there, with the reason; touches nothing.</summary>
    public static LocalS3Store Unavailable(string reason) => new(null, null, reason);

    public static async Task<LocalS3Store> ConnectAsync()
    {
        string? access;
        string? secret;
        try
        {
            access = await ReadSecretKeyAsync("accessKey");
            secret = await ReadSecretKeyAsync("secretKey");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new LocalS3Store(null, null, "kubectl is not available");
        }

        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(secret))
        {
            return new LocalS3Store(null, null, "the Secret octo-infra/seaweedfs-s3 of context kind-kind cannot be read");
        }

        var store = new LocalS3Store(access, secret, string.Empty);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var anonymous = await client.GetAsync(store.HostEndpoint);
            if (anonymous.StatusCode != HttpStatusCode.Forbidden)
            {
                return new LocalS3Store(null, null, $"the store on {store.HostEndpoint} answers an anonymous request with {(int)anonymous.StatusCode}");
            }
        }
        catch (HttpRequestException)
        {
            return new LocalS3Store(null, null, $"nothing answers on {store.HostEndpoint}");
        }
        catch (TaskCanceledException)
        {
            return new LocalS3Store(null, null, $"nothing answers on {store.HostEndpoint}");
        }

        return store;
    }

    /// <summary>A location CrateDB can address, with the real credential: <c>s3://key:secret@endpoint/bucket/prefix</c>.</summary>
    public ArchiveCopyStagingLocation Location(string prefix) => new(
        $"s3://{Uri.EscapeDataString(_accessKey!)}:{Uri.EscapeDataString(_secretKey!)}@{CrateEndpoint}/{Bucket}/{prefix.Trim('/')}",
        new Dictionary<string, string> { ["protocol"] = "http" });

    /// <summary>The objects whose key starts with <paramref name="prefix"/>: key and size.</summary>
    public async Task<List<(string Key, long Size)>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = new List<(string, long)>();
        string? continuation = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2", ["prefix"] = prefix
            };
            if (continuation is not null)
            {
                query["continuation-token"] = continuation;
            }

            using var response = await SendAsync(HttpMethod.Get, $"/{Bucket}", query, cancellationToken);
            response.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var ns = xml.Root!.Name.Namespace;
            foreach (var content in xml.Root.Elements(ns + "Contents"))
            {
                result.Add((content.Element(ns + "Key")!.Value,
                    long.Parse(content.Element(ns + "Size")!.Value, CultureInfo.InvariantCulture)));
            }

            continuation = xml.Root.Element(ns + "NextContinuationToken")?.Value;
        } while (continuation is not null);

        return result;
    }

    /// <summary>Deletes every object under <paramref name="prefix"/>; returns how many.</summary>
    public async Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var objects = await ListAsync(prefix, cancellationToken);
        foreach (var (key, _) in objects)
        {
            using var response = await SendAsync(HttpMethod.Delete, $"/{Bucket}/{key}",
                new SortedDictionary<string, string>(StringComparer.Ordinal), cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        return objects.Count;
    }

    // ---- minimal AWS Signature Version 4 ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, SortedDictionary<string, string> query, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string payloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // SHA-256 of nothing
        var host = $"{HostEndpoint.Host}:{HostEndpoint.Port}";

        var canonicalPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var canonicalQuery = string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = string.Join('\n', method.Method, canonicalPath, canonicalQuery,
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n", signedHeaders, payloadHash);

        var scope = $"{date}/{Region}/s3/aws4_request";
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + _secretKey), date), Region), "s3"), "aws4_request");
        var signature = Hex(Hmac(signingKey, stringToSign));

        var url = $"{HostEndpoint.GetLeftPart(UriPartial.Authority)}{canonicalPath}{(canonicalQuery.Length > 0 ? "?" + canonicalQuery : string.Empty)}";
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={_accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        return await client.SendAsync(request, cancellationToken);
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // ---- the Secret ---------------------------------------------------------------------------------------

    private static async Task<string?> ReadSecretKeyAsync(string key)
    {
        var info = new ProcessStartInfo("kubectl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "--context", KubeContext, "-n", "octo-infra", "get", "secret", "seaweedfs-s3", "-o",
                     $"jsonpath={{.data.{key}}}"
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("kubectl did not start");
        var output = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync(); // discarded: it could echo the request
        await process.WaitForExitAsync();
        return process.ExitCode == 0 && output.Length > 0
            ? Encoding.UTF8.GetString(Convert.FromBase64String(output))
            : null;
    }
}
