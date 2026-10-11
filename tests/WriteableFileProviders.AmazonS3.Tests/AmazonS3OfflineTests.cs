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
/// What needs no server: sign-in, presigned URLs and their lifetimes (D18), the AWS endpoint, the bucket check and its
/// retry, through a fake S3 behind the SDK's HTTP layer.
/// </summary>
[TestClass]
public sealed class AmazonS3OfflineTests
{
    private static AmazonS3FileProviderOptions Aws(string region = "eu-west-1") => new("root", "AKIDEXAMPLE", "secret", "bucket", region);

    private static AmazonS3FileProviderOptions Endpoint() =>
        new("root", "AKIDEXAMPLE", "secret", "bucket", (string?)null) { ServiceUrl = "http://s3.test", ForcePathStyle = true };

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
        new() { Validity = validity, AllowShorterValidity = allowShorter, Permission = access };

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
            await Assert.ThrowsAsync<Exception>(() => provider.GetFileInfoAsync("f.txt"));
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
