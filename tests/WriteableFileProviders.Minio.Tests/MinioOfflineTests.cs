using System.Net;
using System.Net.Http.Headers;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;
using static WriteableFileProviders.S3.Testing.InterceptingHttpClientFactory;

namespace WriteableFileProviders.Minio.Tests;

/// <summary>
/// How the provider builds its client (P23): path-style addressing, the scheme from UseSSL, the region discovered once or
/// given, no storage class; through a fake server behind the SDK's HTTP layer.
/// </summary>
[TestClass]
public sealed class MinioOfflineTests
{
    private static string Location(string region) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><LocationConstraint xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">{region}</LocationConstraint>";

    private static bool IsLocation(HttpRequestMessage request) => request.RequestUri!.Query.Contains("location", StringComparison.Ordinal);

    // Every bucket and object exists, and is empty; the bucket's location is `location`.
    private static (WriteableMinioFileProvider Provider, InterceptingHttpClientFactory Factory, List<string> Signatures) Offline(
        MinioFileProviderOptions options, Func<HttpRequestMessage, HttpResponseMessage?>? answer = null)
    {
        var provider = new WriteableMinioFileProvider(options);
        var signatures = new List<string>();
        var factory = new InterceptingHttpClientFactory
        {
            Intercept = r =>
            {
                lock (signatures)
                    signatures.Add($"{r.Method} {r.RequestUri!.PathAndQuery} {(r.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : "")}");
                var response = answer?.Invoke(r) ?? Empty();
                if (r.Method == HttpMethod.Head && response.StatusCode == HttpStatusCode.OK)
                    response.Headers.ETag = new EntityTagHeaderValue("\"0123456789abcdef\"");
                return Task.FromResult<HttpResponseMessage?>(response);
            },
        };
        provider.Core.HttpClientFactory = factory;
        return (provider, factory, signatures);
    }

    private static MinioFileProviderOptions Options(string? region = null) =>
        new("root", "minio.example.net:9000", "key", "secret", "bucket") { UseSSL = true, Region = region };

    // The region of "Credential=key/date/region/s3/aws4_request" in the Authorization header.
    private static string Scope(string signature) => signature.IndexOf("Credential=", StringComparison.Ordinal) is var at and >= 0
        ? signature[at..].Split('/')[2]
        : "unsigned";

    [TestMethod]
    [DataRow("eu-central-1", "eu-central-1")]
    [DataRow("EU", "eu-west-1")]
    [DataRow("", "us-east-1")]
    public async Task Region_IsDiscoveredOnceAndSignsEveryRequest(string location, string expected)
    {
        var (provider, _, signatures) = Offline(Options(), r => IsLocation(r) ? Xml(HttpStatusCode.OK, Location(location)) : null);
        using (provider)
        {
            Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
            Assert.IsTrue((await provider.GetFileInfoAsync("b.txt")).Exists);
        }
        Assert.AreEqual(1, signatures.Count(IsLocationLine), "The region is asked once");
        // The question is signed for us-east-1, which MinIO answers whatever its region; everything after uses the answer.
        Assert.AreEqual("us-east-1", Scope(signatures.Single(IsLocationLine)));
        foreach (var signature in signatures.Where(s => !IsLocationLine(s)))
            Assert.AreEqual(expected, Scope(signature), signature);
    }

    [TestMethod]
    public async Task Presign_NeverACreateUrl()
    {
        // Its only guard would be the If-None-Match it signs, which older MinIO releases ignore: the gateway issues it.
        var (provider, factory, _) = Offline(Options("us-east-1"));
        using (provider)
        {
            var e = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("new.txt",
                new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create }));
            StringAssert.Contains(e.Message, "Create");
            Assert.AreEqual(0, factory.Requests.Count, "Refused before any request");
            var write = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write });
            Assert.AreEqual("PUT", write.Method);
            Assert.IsFalse(write.Headers.ContainsKey("If-None-Match"));
        }
        using var client = new AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials("key", "secret"),
            new AmazonS3Config { ServiceURL = "https://minio.example.net:9000", ForcePathStyle = true });
        using var given = new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "bucket" }, client);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => given.GetSignedUrlAsync("new.txt",
            new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create, AllowShorterValidity = true }));
    }

    [TestMethod]
    public async Task Region_ThatCannotBeAskedIsUsEast1()
    {
        var (provider, _, signatures) = Offline(Options(), r => IsLocation(r) ? Error(HttpStatusCode.Forbidden, "AccessDenied") : null);
        using (provider)
            Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        Assert.IsTrue(signatures.Where(s => !IsLocationLine(s)).All(s => Scope(s) == "us-east-1"));
    }

    [TestMethod]
    public async Task Region_ThatFailsOtherwiseIsAskedAgain()
    {
        var failing = true;
        var (provider, _, signatures) = Offline(Options(), r => !IsLocation(r) ? null
            : failing ? Error(HttpStatusCode.InternalServerError, "InternalError")
            : Xml(HttpStatusCode.OK, Location("eu-central-1")));
        using (provider)
        {
            // A server error says nothing about the region: the connection fails, and the next call asks again.
            Assert.IsInstanceOfType<AmazonS3Exception>((await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetFileInfoAsync("a.txt"))).InnerException);
            failing = false;
            Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        }
        Assert.AreEqual("eu-central-1", Scope(signatures[^1]));
    }

    [TestMethod]
    public async Task Region_GivenIsNotAsked()
    {
        var (provider, _, signatures) = Offline(Options("ap-south-1"));
        using (provider)
            Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);
        Assert.IsFalse(signatures.Any(IsLocationLine));
        Assert.IsTrue(signatures.All(s => Scope(s) == "ap-south-1"));
    }

    [TestMethod]
    public async Task Requests_ArePathStyleOverTheChosenSchemeWithoutAStorageClass()
    {
        string? storageClass = "unset";
        Uri? putUri = null;
        var (provider, _, _) = Offline(Options("us-east-1"), r =>
        {
            if (r.Method == HttpMethod.Put)
            {
                putUri = r.RequestUri;
                storageClass = r.Headers.TryGetValues("x-amz-storage-class", out var values) ? string.Join(",", values) : null;
            }
            return null;
        });
        using (provider)
        {
            await provider.UpdateFileAsync("dir/f.txt", TestStreams.Text("x"));
            var url = await provider.GetSignedUrlAsync("dir/f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) });
            Assert.AreEqual("https://minio.example.net:9000/bucket/root/dir/f.txt", url.Url.GetLeftPart(UriPartial.Path));
        }
        Assert.AreEqual("https://minio.example.net:9000/bucket/root/dir/f.txt", putUri?.ToString());
        Assert.IsNull(storageClass, "MinIO refuses storage classes it does not know");
    }

    [TestMethod]
    public async Task Anonymous_SendsUnsignedRequestsAndCannotPresign()
    {
        var (provider, _, signatures) = Offline(new MinioFileProviderOptions("root", "localhost:9000", null, null, "bucket") { Region = "us-east-1" });
        using (provider)
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) }));
            Assert.IsTrue((await provider.GetFileInfoAsync("f.txt")).Exists);
        }
        Assert.IsTrue(signatures.All(s => Scope(s) == "unsigned"), string.Join("\n", signatures));
    }

    [TestMethod]
    public async Task Dispose_WhileTheFirstCallConnectsReleasesTheNewClient()
    {
        var asked = new TaskCompletionSource();
        var answer = new TaskCompletionSource();
        var provider = new WriteableMinioFileProvider(Options());
        provider.Core.HttpClientFactory = new InterceptingHttpClientFactory
        {
            Intercept = async r =>
            {
                if (!IsLocation(r))
                    return Empty();
                asked.TrySetResult();
                await answer.Task;
                return Xml(HttpStatusCode.OK, Location(""));
            },
        };
        var first = provider.GetFileInfoAsync("f.txt");
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        provider.Dispose();
        answer.SetResult();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => first);
    }

    private static bool IsLocationLine(string signature) => signature.Contains("?location", StringComparison.Ordinal);
}

/// <summary>The shared offline cases, through the MinIO provider.</summary>
[TestClass]
public sealed class MinioSharedOfflineTests : S3OfflineTests
{
    internal override IWriteableFileProvider CreateOffline(bool createBucketIfMissing = false) =>
        new WriteableMinioFileProvider(new MinioFileProviderOptions("root", "s3.test", "key", "secret", "bucket")
        {
            AllowInsecureHttp = true,
            Region = "us-east-1",
            CreateBucketIfMissing = createBucketIfMissing,
        });

    internal override S3.S3Core CoreOf(IWriteableFileProvider provider) => ((WriteableMinioFileProvider)provider).Core;
}
