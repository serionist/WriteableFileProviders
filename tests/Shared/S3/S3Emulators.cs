using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.S3.Testing;

/// <summary>An S3-compatible server the tests run against, with a bucket and a key pair.</summary>
/// <param name="Name">The server, for messages.</param>
/// <param name="HostAndPort">Its <c>host:port</c>.</param>
/// <param name="AccessKey">The access key.</param>
/// <param name="SecretKey">The secret key.</param>
/// <param name="Bucket">A bucket that exists.</param>
internal sealed record S3Endpoint(string Name, string HostAndPort, string AccessKey, string SecretKey, string Bucket)
{
    /// <summary>The endpoint's URL.</summary>
    public string ServiceUrl => "http://" + HostAndPort;

    /// <summary>
    /// Whether the server listens on loopback, as Testcontainers maps it on a local Docker; a remote Docker host needs the
    /// MinIO provider's opt-in to plain HTTP.
    /// </summary>
    public bool IsLoopback => new Uri(ServiceUrl).IsLoopback;

    /// <summary>A raw client, to arrange and check what the provider cannot.</summary>
    /// <param name="factory">An HTTP client factory, to intercept requests.</param>
    public AmazonS3Client CreateClient(HttpClientFactory? factory = null) => new(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
    {
        ServiceURL = ServiceUrl,
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        HttpClientFactory = factory,
    });

    /// <summary>
    /// Puts an object in the bucket past the providers, under a key they may refuse. Not a key with a control character,
    /// which an XML listing cannot carry back, nor one the server refuses (MinIO refuses a few that S3 takes).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="text">The content.</param>
    /// <returns>False for a key with a control character, or one the server refuses.</returns>
    public async Task<bool> StoreRawAsync(string key, string text)
    {
        if (key.Any(char.IsControl))
            return false;
        using var client = CreateClient();
        try
        {
            await client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, ContentBody = text });
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return false;
        }
    }

    /// <summary>Creates a bucket of its own for a test, which <see cref="DeleteBucketAsync"/> removes.</summary>
    public async Task<string> CreateBucketAsync()
    {
        var bucket = "wfp-" + Guid.NewGuid().ToString("N")[..16];
        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        return bucket;
    }

    /// <summary>Deletes a bucket and everything in it.</summary>
    /// <param name="bucket">The bucket.</param>
    public async Task DeleteBucketAsync(string bucket)
    {
        using var client = CreateClient();
        try
        {
            while (true)
            {
                var page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
                if (page.S3Objects is not { Count: > 0 } objects)
                    break;
                await client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })] });
            }
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchBucket")
        {
        }
    }
}

/// <summary>
/// The S3-compatible servers of the tests, each started once per test run through Testcontainers and removed when the run
/// ends. Without Docker their tests are inconclusive, unless WFP_REQUIRE_DOCKER=1 (CI), which makes them fail.
/// </summary>
internal static class S3Emulators
{
    /// <summary>S3Mock, which checks no signature; its two bugs are declared by the fixtures that use it.</summary>
    public const string S3MockImage = "adobe/s3mock:5.2.3";

    /// <summary>
    /// MinIO, as Chainguard builds it (MinIO publishes source only), pinned by digest. On Docker Desktop it needs to run as
    /// root to write its data folder.
    /// </summary>
    public const string MinioImage = "cgr.dev/chainguard/minio@sha256:7df55d002b698e4494a131b848dd553361bbe58b7e19f62d9623c29af1ba9ccf";

    /// <summary>The bucket every server starts with.</summary>
    public const string Bucket = "wfp-test";

    // MinIO's documented default root credentials and S3Mock's indifference: public test values, not secrets.
    private const string MinioUser = "minioadmin";
    private const string MinioPassword = "minioadmin";

    private static readonly Lazy<Task<S3Endpoint>> _s3Mock = new(StartS3MockAsync);
    private static readonly Lazy<Task<S3Endpoint>> _minio = new(() => StartMinioAsync(null));
    private static readonly Lazy<Task<S3Endpoint>> _minioInRegion = new(() => StartMinioAsync("eu-central-1"));

    /// <summary>Whether a missing Docker fails the tests instead of leaving them inconclusive.</summary>
    public static bool DockerRequired => Environment.GetEnvironmentVariable("WFP_REQUIRE_DOCKER") == "1";

    /// <summary>S3Mock 5.2.3.</summary>
    public static S3Endpoint S3Mock => Started(_s3Mock, "S3Mock");

    /// <summary>MinIO, with no site region.</summary>
    public static S3Endpoint Minio => Started(_minio, "MinIO");

    /// <summary>MinIO with the site region eu-central-1, which refuses requests signed for another region.</summary>
    public static S3Endpoint MinioInEuCentral1 => Started(_minioInRegion, "MinIO (eu-central-1)");

    private static async Task<S3Endpoint> StartS3MockAsync()
    {
        var container = new ContainerBuilder(S3MockImage)
            .WithEnvironment("COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS", Bucket)
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
            .Build();
        await container.StartAsync();
        return new S3Endpoint("S3Mock", $"{container.Hostname}:{container.GetMappedPublicPort(9090)}", "test", "test", Bucket);
    }

    private static async Task<S3Endpoint> StartMinioAsync(string? siteRegion)
    {
        var builder = new ContainerBuilder(MinioImage)
            .WithCreateParameterModifier(p => p.User = "0")
            .WithCommand("server", "/data")
            .WithEnvironment("MINIO_ROOT_USER", MinioUser)
            .WithEnvironment("MINIO_ROOT_PASSWORD", MinioPassword)
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/ready")));
        if (siteRegion is not null)
            builder = builder.WithEnvironment("MINIO_SITE_REGION", siteRegion);
        IContainer container = builder.Build();
        await container.StartAsync();
        var endpoint = new S3Endpoint(siteRegion is null ? "MinIO" : $"MinIO ({siteRegion})",
            $"{container.Hostname}:{container.GetMappedPublicPort(9000)}", MinioUser, MinioPassword, Bucket);
        using var client = new AmazonS3Client(new BasicAWSCredentials(MinioUser, MinioPassword), new AmazonS3Config
        {
            ServiceURL = endpoint.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = siteRegion ?? "us-east-1",
        });
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        return endpoint;
    }

    private static S3Endpoint Started(Lazy<Task<S3Endpoint>> endpoint, string name)
    {
        try
        {
            return endpoint.Value.GetAwaiter().GetResult();
        }
        catch (Exception e) when (!DockerRequired)
        {
            Assert.Inconclusive($"{name} could not start through Testcontainers (is Docker running?): {e.Message}");
            throw;
        }
    }
}
