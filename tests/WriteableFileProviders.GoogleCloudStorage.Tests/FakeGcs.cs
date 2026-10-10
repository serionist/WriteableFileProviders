using System.Net.Http.Json;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testcontainers.FakeGcsServer;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>
/// The fake-gcs-server emulator, started once per test run through Testcontainers and removed when the run ends, and a
/// service account made up for it. Without Docker the tests that need it are inconclusive, unless WFP_REQUIRE_DOCKER=1
/// (set in CI), which makes them fail.
/// </summary>
/// <remarks>
/// The emulator checks no credential, but a client with a service account key signs every request with a self-signed
/// JWT, never asking Google for a token, so the production path (key, endpoint, signer) runs against it unchanged. The
/// key is generated for the run: nothing secret is committed.
/// </remarks>
internal static class FakeGcs
{
    public const string Image = "fsouza/fake-gcs-server:1.56.1";
    public const string ProjectId = "wfp-test";
    public const string ClientEmail = "wfp-test@wfp-test.iam.gserviceaccount.com";

    private static readonly Lazy<Task<FakeGcsServerContainer>> _container = new(StartAsync);

    /// <summary>A private key generated for this run, in PEM form.</summary>
    public static string PrivateKey { get; } = CreateKey();

    public static bool DockerRequired => Environment.GetEnvironmentVariable("WFP_REQUIRE_DOCKER") == "1";

    /// <summary>The emulator's root URL, such as http://127.0.0.1:49153.</summary>
    public static string Endpoint => new Uri(ConnectionString).GetLeftPart(UriPartial.Authority);

    /// <summary>The client base URI the module gives: the root followed by /storage/v1/.</summary>
    public static string ConnectionString => Container.GetConnectionString();

    private static FakeGcsServerContainer Container
    {
        get
        {
            try
            {
                return _container.Value.GetAwaiter().GetResult();
            }
            catch (Exception e) when (!DockerRequired)
            {
                Assert.Inconclusive($"fake-gcs-server could not start through Testcontainers (is Docker running?): {e.Message}");
                throw;
            }
        }
    }

    /// <summary>The made-up service account, as a credential.</summary>
    public static GoogleCredential Credential() =>
        new ServiceAccountCredential(new ServiceAccountCredential.Initializer(ClientEmail) { ProjectId = ProjectId }.FromPrivateKey(PrivateKey)).ToGoogleCredential();

    /// <summary>The made-up service account as a key file, the way Google issues one.</summary>
    public static string CredentialsJson(string? projectId = ProjectId) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["type"] = "service_account",
            ["project_id"] = projectId,
            ["private_key_id"] = "0123456789abcdef",
            ["private_key"] = PrivateKey,
            ["client_email"] = ClientEmail,
            ["client_id"] = "1",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        });

    /// <summary>A client of the emulator: signed in with the made-up account, or unauthenticated.</summary>
    /// <param name="authenticated">Whether to sign in, which also lets the client sign URLs.</param>
    /// <param name="httpClientFactory">An HTTP seam, such as <see cref="FaultInjection"/>.</param>
    public static StorageClient CreateClient(bool authenticated = true, Google.Apis.Http.IHttpClientFactory? httpClientFactory = null)
    {
        var builder = new StorageClientBuilder { BaseUri = ConnectionString };
        if (authenticated)
            builder.GoogleCredential = Credential();
        else
            builder.UnauthenticatedAccess = true;
        if (httpClientFactory is not null)
            builder.HttpClientFactory = httpClientFactory;
        return builder.Build();
    }

    /// <summary>Creates a bucket with a unique name.</summary>
    /// <param name="prefix">The start of its name.</param>
    public static string CreateBucket(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 63)];
        using var client = CreateClient();
        client.CreateBucket(ProjectId, name);
        return name;
    }

    /// <summary>A bucket name that exists nowhere.</summary>
    public static string MissingBucketName() => $"wfp-missing-{Guid.NewGuid():N}"[..30];

    /// <summary>Options for the emulator through the production path: the made-up key and the endpoint.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="root">The root folder, or null.</param>
    public static GoogleCloudStorageFileProviderOptions Options(string bucket, string? root = null) => new()
    {
        BucketName = bucket,
        ClientEmail = ClientEmail,
        PrivateKey = PrivateKey,
        ProjectId = ProjectId,
        Endpoint = Endpoint,
        RootDirectoryPath = root,
    };

    private static async Task<FakeGcsServerContainer> StartAsync()
    {
        var container = new FakeGcsServerBuilder(Image).Build();
        await container.StartAsync().ConfigureAwait(false);
        // Signed URLs reach the emulator only when the request's host is its public host (storage.googleapis.com by
        // default): make it the host the tests reach it on.
        var connection = new Uri(container.GetConnectionString());
        using var http = new HttpClient();
        using var response = await http.PutAsJsonAsync(new Uri(connection, "/_internal/config"), new { publicHost = connection.Host }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return container;
    }

    private static string CreateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
