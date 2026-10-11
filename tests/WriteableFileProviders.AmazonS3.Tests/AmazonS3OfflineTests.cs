using System.Net;
using System.Net.Http.Headers;
using System.Web;
using Amazon;
using Amazon.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;
using static WriteableFileProviders.S3.Testing.InterceptingHttpClientFactory;

namespace WriteableFileProviders.AmazonS3.Tests;

/// <summary>
/// What needs no server: sign-in, presigned URLs and their lifetimes, the AWS endpoint, the bucket check and its
/// retry, through a fake S3 behind the SDK's HTTP layer.
/// </summary>
[TestClass]
public sealed class AmazonS3OfflineTests
{
    private static AmazonS3FileProviderOptions Aws(string region = "eu-west-1") => new("root", "AKIDEXAMPLE", "secret", "bucket", region);

    private static AmazonS3FileProviderOptions Endpoint() =>
        new("root", "AKIDEXAMPLE", "secret", "bucket", (string?)null) { ServiceUrl = "http://s3.test", ForcePathStyle = true, AllowInsecureHttp = true };

    // Every bucket and object exists, and is empty.
    private static HttpResponseMessage Exists(HttpRequestMessage request)
    {
        var response = Empty();
        if (request.Method == HttpMethod.Head)
        {
            response.Headers.ETag = new EntityTagHeaderValue("\"0123456789abcdef\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        }
        return response;
    }

    private static (WriteableAmazonS3FileProvider Provider, InterceptingHttpClientFactory Factory) Offline(
        AmazonS3FileProviderOptions options, AWSCredentials? credentials = null, Func<HttpRequestMessage, HttpResponseMessage?>? answer = null)
    {
        var provider = credentials is null ? new WriteableAmazonS3FileProvider(options) : new WriteableAmazonS3FileProvider(options, credentials);
        var factory = new InterceptingHttpClientFactory { Intercept = r => Task.FromResult<HttpResponseMessage?>(answer?.Invoke(r) ?? Exists(r)) };
        provider.Core.HttpClientFactory = factory;
        return (provider, factory);
    }

    private static SignedUrlOptions For(TimeSpan validity, bool allowShorter = false, SignedUrlAccess access = SignedUrlAccess.Read) =>
        new() { Validity = validity, AllowShorterValidity = allowShorter, Access = access };

    [TestMethod]
    public async Task Presign_LongTermKeysLiveAsAskedOnAwsOverHttps()
    {
        var (provider, _) = Offline(Aws());
        using (provider)
        {
            var before = DateTimeOffset.UtcNow;
            var url = await provider.GetSignedUrlAsync("dir/f.txt", For(TimeSpan.FromHours(1)));
            Assert.AreEqual(Uri.UriSchemeHttps, url.Url.Scheme);
            Assert.AreEqual("bucket.s3.eu-west-1.amazonaws.com", url.Url.Host);
            Assert.AreEqual("/root/dir/f.txt", url.Url.AbsolutePath);
            var query = HttpUtility.ParseQueryString(url.Url.Query);
            Assert.AreEqual("3600", query["X-Amz-Expires"]);
            StringAssert.Contains(query["X-Amz-Credential"], "/eu-west-1/s3/aws4_request");
            Assert.IsNull(query["X-Amz-Security-Token"]);
            Assert.IsTrue(url.ExpiresOn >= before.AddHours(1) && url.ExpiresOn <= DateTimeOffset.UtcNow.AddHours(1), $"{url.ExpiresOn}");
            Assert.AreEqual(0, url.Headers.Count);
        }
    }

    [TestMethod]
    public async Task Presign_SessionCredentialsMayExpireFirst()
    {
        var (provider, _) = Offline(Aws(), new SessionAWSCredentials("AKIDEXAMPLE", "secret", "session-token"));
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5))));
            StringAssert.Contains(e.Message, "AllowShorterValidity");
            var url = await provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5), allowShorter: true));
            Assert.IsNull(url.ExpiresOn, "The expiry of a session token is not known");
            Assert.AreEqual("session-token", HttpUtility.ParseQueryString(url.Url.Query)["X-Amz-Security-Token"]);
        }
    }

    [TestMethod]
    public async Task Presign_ExpiringCredentialsCutTheUrl()
    {
        var expiry = DateTime.UtcNow.AddHours(1);
        var (provider, _) = Offline(Aws(), new ExpiringCredentials(expiry));
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromHours(2))));
            StringAssert.Contains(e.Message, "expires");
            var cut = await provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromHours(2), allowShorter: true));
            Assert.IsNotNull(cut.ExpiresOn);
            Assert.IsTrue(cut.ExpiresOn <= new DateTimeOffset(expiry) && cut.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(30), $"{cut.ExpiresOn}");
            var full = await provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(10)));
            Assert.IsTrue(full.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(10), $"{full.ExpiresOn}");
        }
    }

    [TestMethod]
    public async Task Presign_DefaultChainResolvesOnFirstUse()
    {
        var names = new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            // AWS's documented example key, not a credential.
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIAIOSFODNN7EXAMPLE");
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
            Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", "example-token");
            var options = new AmazonS3FileProviderOptions { BucketName = "bucket", BucketRegion = "us-east-1", UseDefaultCredentials = true, IsAnonymus = true };
            var (provider, factory) = Offline(options);
            using (provider)
            {
                Assert.AreEqual(0, factory.Requests.Count);
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5))));
                var url = await provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5), allowShorter: true, access: SignedUrlAccess.Write));
                StringAssert.StartsWith(HttpUtility.ParseQueryString(url.Url.Query)["X-Amz-Credential"], "AKIAIOSFODNN7EXAMPLE/");
                Assert.IsNull(url.ExpiresOn);
            }
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [TestMethod]
    [DataRow("AWS_ENDPOINT_URL_S3")]
    [DataRow("AWS_ENDPOINT_URL")]
    public async Task AnEndpointInTheSdksConfiguration_IsANamedEndpoint(string variable)
    {
        // The SDK redirects a client it builds to an endpoint from its own configuration: the provider treats it as it
        // treats ServiceUrl, for the plain-http rule, the conditions, create URLs and requester-pays.
        string[] names = ["AWS_ENDPOINT_URL_S3", "AWS_ENDPOINT_URL"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(variable, "http://minio.internal:9000");
            var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(Aws()));
            StringAssert.Contains(e.Message, "AllowInsecureHttp");
            StringAssert.Contains(e.Message, "minio.internal");
            Assert.AreEqual("options", e.ParamName);

            string? payer = "unset";
            var options = Aws();
            options.AllowInsecureHttp = true;
            var (provider, factory) = Offline(options, answer: r =>
            {
                if (r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.EndsWith("/f.txt", StringComparison.Ordinal))
                    payer = r.Headers.TryGetValues("x-amz-request-payer", out var values) ? string.Join(",", values) : null;
                return null;
            });
            using (provider)
            {
                Assert.IsFalse(provider.Core.ChecksAtomically, "Conditions toward the configured endpoint are checked first");
                Assert.IsNull(provider.Core.BucketLocation);
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                    provider.GetSignedUrlAsync("new.txt", For(TimeSpan.FromMinutes(5), access: SignedUrlAccess.Create)));
                Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
                Assert.IsNull(payer, "No requester-pays header toward a server that is not S3");
                Assert.AreEqual(Uri.UriSchemeHttp, (await provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5)))).Url.Scheme);
            }

            // One of S3's own host names is still S3 for requester-pays and a created bucket's location.
            Environment.SetEnvironmentVariable(variable, "https://s3.eu-west-1.amazonaws.com");
            using var s3 = new WriteableAmazonS3FileProvider(Aws());
            Assert.IsFalse(s3.Core.ChecksAtomically);
            Assert.AreEqual("eu-west-1", s3.Core.BucketLocation);

            // An endpoint that is neither http nor https is refused, as a ServiceUrl is.
            Environment.SetEnvironmentVariable(variable, "ftp://files.internal");
            StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(Aws())).Message, "AWS_ENDPOINT_URL_S3");
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [TestMethod]
    public async Task AnEndpointConfiguredAfterTheProviderIsBuilt_DoesNotRedirectIt()
    {
        // The configured endpoint is read when the provider is built; one set later, before the first call, is not
        // followed: the client the provider builds for AWS ignores the SDK's configured endpoints.
        var saved = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3");
        try
        {
            Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_S3", null);
            var hosts = new List<string>();
            var (provider, _) = Offline(Aws(), answer: r =>
            {
                lock (hosts)
                    hosts.Add(r.RequestUri!.Host);
                return null;
            });
            using (provider)
            {
                Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_S3", "http://minio.internal:9000");
                Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
                Assert.IsTrue(provider.Core.ChecksAtomically);
            }
            Assert.IsTrue(hosts.Count > 0);
            CollectionAssert.DoesNotContain(hosts, "minio.internal");
            Assert.IsTrue(hosts.All(h => h.EndsWith(".amazonaws.com", StringComparison.Ordinal)), string.Join(", ", hosts));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_S3", saved);
        }
    }

    [TestMethod]
    public async Task Presign_CreateUrlsOnlyWhereTheStoreEnforcesThem()
    {
        // AWS enforces the If-None-Match a create URL signs; an S3-compatible server behind a named endpoint may ignore
        // it and let the URL overwrite a file, so there the provider refuses, and the URL service falls back to the
        // gateway.
        var (aws, _) = Offline(Aws());
        using (aws)
        {
            var create = await aws.GetSignedUrlAsync("new.txt", For(TimeSpan.FromMinutes(5), access: SignedUrlAccess.Create));
            Assert.AreEqual("PUT", create.Method);
            Assert.AreEqual("*", create.Headers["If-None-Match"]);
            StringAssert.Contains(HttpUtility.ParseQueryString(create.Url.Query)["X-Amz-SignedHeaders"], "if-none-match");
        }
        var (endpoint, factory) = Offline(Endpoint());
        using (endpoint)
        {
            var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                endpoint.GetSignedUrlAsync("new.txt", For(TimeSpan.FromMinutes(5), access: SignedUrlAccess.Create)));
            StringAssert.Contains(e.Message, "Create");
            Assert.AreEqual(0, factory.Requests.Count, "Refused before any request");
            Assert.AreEqual("PUT", (await endpoint.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5), access: SignedUrlAccess.Write))).Method);
            Assert.AreEqual("GET", (await endpoint.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5)))).Method);
        }
        using var endpointClient = new Amazon.S3.AmazonS3Client(new BasicAWSCredentials("AKIDEXAMPLE", "secret"),
            new Amazon.S3.AmazonS3Config { ServiceURL = "https://minio.example.com", ForcePathStyle = true });
        using var given = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "bucket" }, endpointClient);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            given.GetSignedUrlAsync("new.txt", For(TimeSpan.FromMinutes(5), allowShorter: true, access: SignedUrlAccess.Create)));
    }

    [TestMethod]
    public async Task Presign_AnonymousIsNotSupported()
    {
        var (provider, factory) = Offline(new AmazonS3FileProviderOptions("root", "bucket", "eu-west-1"));
        using (provider)
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromMinutes(5))));
            Assert.AreEqual(0, factory.Requests.Count, "Refused before any request");
            Assert.IsFalse((await provider.GetFileInfoAsync("missing")).IsDirectory);
        }
    }

    [TestMethod]
    public async Task Presign_ASkewedSdkClockInASigV4OnlyRegion()
    {
        // The SDK measures expiry on its clock, corrected for the server's skew. Behind ours, 7 days exceed SigV4's limit,
        // and in a region without SigV2 the SDK throws (S3OfflineTests covers the SigV2 fallback elsewhere).
        var saved = AWSConfigs.ManualClockCorrection;
        try
        {
            AWSConfigs.ManualClockCorrection = TimeSpan.FromMinutes(-10);
            var (provider, _) = Offline(Aws("eu-central-1"));
            using (provider)
            {
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromDays(7))));
                StringAssert.Contains((await provider.GetSignedUrlAsync("f.txt", For(TimeSpan.FromDays(6)))).Url.Query, "X-Amz-Signature=");
            }
        }
        finally
        {
            AWSConfigs.ManualClockCorrection = saved;
        }
    }

    [TestMethod]
    public async Task Requests_OnAwsAcceptRequesterPaysAndElsewhereDoNot()
    {
        string? payer = "unset";
        HttpResponseMessage? Capture(HttpRequestMessage r)
        {
            if (r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.EndsWith("/f.txt", StringComparison.Ordinal))
                payer = r.Headers.TryGetValues("x-amz-request-payer", out var values) ? string.Join(",", values) : null;
            return null;
        }
        var (aws, _) = Offline(Aws(), answer: Capture);
        using (aws)
            Assert.IsTrue((await aws.GetFileInfoAsync("f.txt")).Exists);
        Assert.AreEqual("requester", payer);
        var (endpoint, _) = Offline(Endpoint(), answer: Capture);
        using (endpoint)
            Assert.IsTrue((await endpoint.GetFileInfoAsync("f.txt")).Exists);
        Assert.IsNull(payer);
    }

    [TestMethod]
    [DataRow("http://s3.test", false, true)]
    [DataRow("http://localhost:9000", false, true)]
    [DataRow("http://localhost:9000", false, false)]
    [DataRow("https://bucket.vpce-0a1b2c3d-4e5f6a7b.s3.eu-west-1.vpce.amazonaws.com", true, true)]
    [DataRow("https://bucket.vpce-0a1b2c3d-4e5f6a7b.s3.eu-west-1.vpce.amazonaws.com", true, false)]
    [DataRow("http://my-minio-123.eu-west-1.elb.amazonaws.com", false, false)]
    [DataRow("http://ec2-3-250-1-2.eu-west-1.compute.amazonaws.com:9000", false, true)]
    [DataRow("https://abcdef1234.execute-api.eu-west-1.amazonaws.com", false, false)]
    [DataRow("https://s3-eu-west-1.amazonaws.com", false, true)]
    public async Task AnEndpoint_IsS3OnlyOnS3HostNames(string endpoint, bool s3, bool givenClient)
    {
        // Any endpoint named, in the options' ServiceUrl or a given client's ServiceURL, gets its conditions checked first,
        // S3's own included: a load balancer, an instance or an API under amazonaws.com may front another server. An
        // endpoint that is one of S3's own host names, such as a VPC endpoint, still sends the requester-pays header and a
        // created bucket's location; any other, a legacy dashed S3 name included, sends neither.
        string? payer = "unset";
        string? newBucket = null;
        var missing = true;
        async Task<HttpResponseMessage?> Answer(HttpRequestMessage r)
        {
            if (r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.Trim('/') == "bucket" && missing)
                return Empty(HttpStatusCode.NotFound);
            if (r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath.Trim('/') == "bucket")
            {
                missing = false;
                newBucket = r.Content is null ? "" : await r.Content.ReadAsStringAsync();
                return Empty();
            }
            if (r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.EndsWith("/f.txt", StringComparison.Ordinal))
                payer = r.Headers.TryGetValues("x-amz-request-payer", out var values) ? string.Join(",", values) : null;
            return Exists(r);
        }
        var factory = new InterceptingHttpClientFactory { Intercept = Answer };
        using var client = new Amazon.S3.AmazonS3Client(new BasicAWSCredentials("AKIDEXAMPLE", "secret"),
            new Amazon.S3.AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, HttpClientFactory = factory });
        var options = new AmazonS3FileProviderOptions("root", "AKIDEXAMPLE", "secret", "bucket", "eu-west-1")
        {
            CreateBucketIfMissing = true,
            ForcePathStyle = true,
            AllowInsecureHttp = true,
        };
        WriteableAmazonS3FileProvider provider;
        if (givenClient)
        {
            provider = new WriteableAmazonS3FileProvider(options, client);
        }
        else
        {
            options.ServiceUrl = endpoint;
            provider = new WriteableAmazonS3FileProvider(options);
            provider.Core.HttpClientFactory = factory;
        }
        using (provider)
        {
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
            Assert.AreEqual(s3 ? "requester" : null, payer, "The requester-pays header");
            Assert.IsNotNull(newBucket, "The bucket was created");
            // The location the provider gives; for a host under amazonaws.com the SDK may add one of its own, read from
            // the host, so the request shows it only off AWS.
            Assert.AreEqual(s3 ? "eu-west-1" : null, provider.Core.BucketLocation);
            if (!new Uri(endpoint).Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
                Assert.IsFalse(newBucket.Contains("<LocationConstraint>", StringComparison.Ordinal), newBucket);
            Assert.IsFalse(provider.Core.ChecksAtomically, "Conditions toward a named endpoint are checked first");
        }
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("https://s3.amazonaws.com", true)]
    [DataRow("https://S3.EU-WEST-1.AMAZONAWS.COM", true)]
    [DataRow("https://s3.eu-west-1.amazonaws.com.", true)]
    [DataRow("https://bucket.s3.eu-west-1.amazonaws.com", true)]
    [DataRow("https://s3.dualstack.eu-west-1.amazonaws.com", true)]
    [DataRow("https://bucket.vpce-0a1b2c3d-4e5f6a7b.s3.eu-west-1.vpce.amazonaws.com", true)]
    [DataRow("https://s3.cn-north-1.amazonaws.com.cn", true)]
    [DataRow("s3.eu-west-1.amazonaws.com", true)]
    [DataRow("https://s3-fips.us-gov-west-1.amazonaws.com", true)]
    [DataRow("https://s3-fips.us-east-1.amazonaws.com", true)]
    [DataRow("https://S3-FIPS.dualstack.us-east-1.amazonaws.com", true)]
    [DataRow("https://bucket.s3-fips.us-east-1.amazonaws.com", true)]
    [DataRow("https://s3-fips-x.us-east-1.amazonaws.com", false)]
    [DataRow("https://s3-fips.example.com", false)]
    [DataRow("https://s3-eu-west-1.amazonaws.com", false)]
    [DataRow("https://s3-external-1.amazonaws.com", false)]
    [DataRow("http://my-minio-123.eu-west-1.elb.amazonaws.com", false)]
    [DataRow("http://ec2-3-250-1-2.eu-west-1.compute.amazonaws.com:9000", false)]
    [DataRow("https://abcdef1234.execute-api.eu-west-1.amazonaws.com", false)]
    [DataRow("http://localhost:9000", false)]
    [DataRow("https://minio.example.com", false)]
    [DataRow("https://s3.example.com", false)]
    [DataRow("https://evilamazonaws.com", false)]
    [DataRow("https://s3.amazonaws.com.example.org", false)]
    [DataRow("http://[not a host", false)]
    public void IsS3Endpoint_TakesS3HostNamesOnly(string? endpoint, bool s3) =>
        Assert.AreEqual(s3, WriteableAmazonS3FileProvider.IsS3Endpoint(endpoint), endpoint);

    [TestMethod]
    public async Task BucketCheck_ForbiddenIsNotMissing()
    {
        var (provider, _) = Offline(Endpoint(), answer: r => r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.Trim('/') == "bucket"
            ? Empty(HttpStatusCode.Forbidden)
            : null);
        using (provider)
        {
            var info = await provider.GetFileInfoAsync("f.txt");
            Assert.IsTrue(info.Exists);
            Assert.AreEqual("text/plain", info.ContentType);
        }
    }

    [TestMethod]
    public async Task BucketCheck_ThatFailsIsRetriedByTheNextCall()
    {
        var failing = true;
        var (provider, factory) = Offline(Endpoint(), answer: r => failing && r.Method == HttpMethod.Head && r.RequestUri!.AbsolutePath.Trim('/') == "bucket"
            ? throw new HttpRequestException("Injected network failure")
            : null);
        using (provider)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("f.txt"));
            var failedChecks = BucketChecks(factory);
            Assert.IsTrue(failedChecks > 0);
            failing = false;
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
            Assert.IsTrue((await provider.GetFileInfoAsync("g.txt")).Exists);
            Assert.AreEqual(failedChecks + 1, BucketChecks(factory), "Once it succeeded, the bucket is not checked again");
        }
    }

    private static int BucketChecks(InterceptingHttpClientFactory factory) =>
        factory.Requests.Count(r => r.TrimEnd('/') == "HEAD /bucket");

    [TestMethod]
    public async Task ReadFileVersion_ValidatesThePathAndMapsAMissingVersion()
    {
        var (provider, factory) = Offline(Endpoint(), answer: r => r.Method == HttpMethod.Get ? Error(HttpStatusCode.NotFound, "NoSuchVersion") : null);
        using (provider)
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileVersionAsync("v1", ""));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileVersionAsync("v1", "a/../b"));
            Assert.AreEqual(0, factory.Requests.Count);
            var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileVersionAsync("v1", "dir/f.txt"));
            Assert.IsNotNull(e.InnerException);
            Assert.IsTrue(factory.Requests.Any(r => r.Contains("/bucket/root/dir/f.txt?versionId=v1", StringComparison.Ordinal)), string.Join("\n", factory.Requests));
        }
    }

    [TestMethod]
    public async Task ReadFileVersion_FollowsTheErrorContract()
    {
        var (provider, _) = Offline(Endpoint(), answer: r => r.Method == HttpMethod.Get ? Error(HttpStatusCode.Forbidden, "AccessDenied") : null);
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.ReadFileVersionAsync("v1", "dir/f.txt"));
            Assert.IsInstanceOfType<Amazon.S3.AmazonS3Exception>(e.InnerException);
        }
    }

    [TestMethod]
    public async Task Cancellation_BeforeFirstUseSendsNothing()
    {
        var (provider, factory) = Offline(Endpoint());
        using (provider)
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetFileInfoAsync("f.txt", cancelled.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x"), cancellationToken: cancelled.Token));
            Assert.AreEqual(0, factory.Requests.Count);
        }
    }

    [TestMethod]
    public async Task Dispose_ReleasesTheOwnClientOnce()
    {
        var (provider, _) = Offline(Endpoint());
        Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
        provider.Dispose();
        provider.Dispose();
        await provider.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetFileInfoAsync("f.txt"));
        var (unused, _) = Offline(Endpoint());
        await unused.DisposeAsync();
    }

    /// <summary>A refreshing credential, as STS or instance metadata give, that expires at a known time.</summary>
    /// <param name="expiry">When it expires.</param>
    private sealed class ExpiringCredentials(DateTime expiry) : RefreshingAWSCredentials
    {
        protected override CredentialsRefreshState GenerateNewCredentials() =>
            new(new ImmutableCredentials("AKIDEXAMPLE", "secret", "token"), expiry);
    }
}

/// <summary>The shared offline cases, through the Amazon S3 provider on AWS (eu-west-1).</summary>
[TestClass]
public sealed class AmazonS3SharedOfflineTests : S3OfflineTests
{
    // The SDK sends eu-west-1 under its legacy location name, which S3 accepts.
    protected override string? ExpectedLocationConstraint => "EU";

    protected override bool ChecksAtomically => true;

    internal override IWriteableFileProvider CreateOffline(bool createBucketIfMissing = false) =>
        new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions("root", "AKIDEXAMPLE", "secret", "bucket", "eu-west-1")
        {
            CreateBucketIfMissing = createBucketIfMissing,
        });

    internal override S3.S3Core CoreOf(IWriteableFileProvider provider) => ((WriteableAmazonS3FileProvider)provider).Core;
}
