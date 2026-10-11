using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Minio.Tests;

/// <summary>The options and their validation, plain http toward another host included; no network.</summary>
[TestClass]
public sealed class MinioOptionsTests
{
    private static MinioFileProviderOptions Options(string hostAndPort, bool useSsl = false, bool allowInsecureHttp = false) =>
        new("root", hostAndPort, "key", "secret", "bucket") { UseSSL = useSsl, AllowInsecureHttp = allowInsecureHttp };

    [TestMethod]
    public void Construction_DoesNoNetworkIO()
    {
        // Nobody listens there: building and disposing the provider must not reach for it.
        var options = Options("127.0.0.1:1");
        using var provider = new WriteableMinioFileProvider(options);
        Assert.AreSame(options, provider.Options);
        Assert.AreEqual("bucket", provider.Core.Bucket);
    }

    [TestMethod]
    [DataRow("localhost:9000")]
    [DataRow("LOCALHOST")]
    [DataRow("127.0.0.1:9000")]
    [DataRow("127.0.0.42:9000")]
    [DataRow("[::1]:9000")]
    public void PlainHttp_ToLoopbackNeedsNoOptIn(string hostAndPort)
    {
        using var provider = new WriteableMinioFileProvider(Options(hostAndPort));
    }

    [TestMethod]
    public void PlainHttp_ToAnotherHostNeedsTheOptIn()
    {
        // A configuration that forgot UseSSL must not send files in clear text across the network.
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(Options("minio.internal:9000")));
        StringAssert.Contains(e.Message, "UseSSL");
        StringAssert.Contains(e.Message, "AllowInsecureHttp");
        Assert.AreEqual("options", e.ParamName);
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(Options("10.0.0.5:9000")));
        using var allowed = new WriteableMinioFileProvider(Options("minio.internal:9000", allowInsecureHttp: true));
        using var tls = new WriteableMinioFileProvider(Options("minio.example.net:9000", useSsl: true));
        using var tlsDefaultPort = new WriteableMinioFileProvider(Options("minio.example.net", useSsl: true));
    }

    [TestMethod]
    public void PlainHttp_ThroughAGivenClient_FollowsTheSameRule()
    {
        // A client's own endpoint, as the options' host: plain HTTP toward another host needs the opt-in.
        using var remote = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://minio.internal:9000", ForcePathStyle = true });
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "b" }, remote));
        StringAssert.Contains(e.Message, "AllowInsecureHttp");
        Assert.AreEqual("options", e.ParamName);
        using (new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "b", AllowInsecureHttp = true }, remote))
        {
        }
        using var local = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true });
        using (new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "b" }, local))
        {
        }
        using var tls = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "https://minio.internal:9000", ForcePathStyle = true });
        using (new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "b" }, tls))
        {
        }
    }

    [TestMethod]
    [DataRow("http://minio.example.net:9000")]
    [DataRow("https://minio.example.net")]
    [DataRow("minio.example.net:9000/bucket")]
    [DataRow("minio.example.net:9000/")]
    [DataRow("minio.example.net:9000?x=1")]
    [DataRow("user@minio.example.net:9000")]
    [DataRow("minio.example.net:notaport")]
    [DataRow("")]
    [DataRow("   ")]
    public void HostAndPort_TakesHostAndPortOnly(string hostAndPort)
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(Options(hostAndPort, useSsl: true)));
        StringAssert.Contains(e.Message, "HostAndPort");
    }

    [TestMethod]
    public void Options_AreValidated()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableMinioFileProvider(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableMinioFileProvider(Options("localhost:9000"), null!));
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(new MinioFileProviderOptions("r", "localhost:9000", "k", "s", null)));
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(new MinioFileProviderOptions("r", null, "k", "s", "b")));
        var unpaired = Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(new MinioFileProviderOptions("r", "localhost:9000", "k", null, "b")));
        StringAssert.Contains(unpaired.Message, "SecretKey");
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(new MinioFileProviderOptions("r", "localhost:9000", null, "s", "b")));
        // No keys at all: anonymous requests, as the 2026 provider sent them.
        using var anonymous = new WriteableMinioFileProvider(new MinioFileProviderOptions("r", "localhost:9000", null, null, "b"));

        // A client given by the caller brings its own server and credentials.
        using var client = new AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1);
        using var given = new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "b" }, client);
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableMinioFileProvider(new MinioFileProviderOptions(), client));
    }

    [TestMethod]
    public void NewOptions_AreWrittenOnlyWhenSet()
    {
        // A configuration that uses none of the 2027 options keeps its 2026 shape.
        string[] added = ["AllowInsecureHttp", "Region", "CreateBucketIfMissing"];
        using (var plain = new WriteableMinioFileProvider(Options("localhost:9000")))
        {
            var json = plain.ToJson();
            Assert.IsFalse(added.Any(json.ContainsKey), json.ToJsonString());
        }
        using var set = new WriteableMinioFileProvider(new MinioFileProviderOptions("root", "minio.internal:9000", "key", "secret", "bucket")
        {
            AllowInsecureHttp = true,
            Region = "eu-central-1",
            CreateBucketIfMissing = true,
        });
        var written = set.ToJson();
        Assert.IsTrue(added.All(written.ContainsKey), written.ToJsonString());
        using var read = (WriteableMinioFileProvider)WriteableFileProviderSerializer.FromJson(written);
        Assert.AreEqual("eu-central-1", read.Options.Region);
        Assert.IsTrue(read.Options.AllowInsecureHttp && read.Options.CreateBucketIfMissing);
    }

    [TestMethod]
    public void Conditions_AreAlsoCheckedFirst()
    {
        // MinIO may ignore a condition (on copies and deletes it does), whatever the client.
        using (var own = new WriteableMinioFileProvider(Options("localhost:9000")))
            Assert.IsFalse(own.Core.ChecksAtomically);
        using var client = new AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1);
        using var given = new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = "b" }, client);
        Assert.IsFalse(given.Core.ChecksAtomically);
    }

    [TestMethod]
    public void Provider_IsSealed() =>
        // A custom provider derives from WriteableFileProvider<TOptions>, not from a provider.
        Assert.IsTrue(typeof(WriteableMinioFileProvider).IsSealed);

    [TestMethod]
    public void Options_KeepTheirConstructorValues()
    {
        var options = new MinioFileProviderOptions("a/b", "host:1", "k", "s", "bucket");
        Assert.AreEqual("host:1", options.HostAndPort);
        Assert.AreEqual("k", options.AccessKey);
        Assert.AreEqual("s", options.SecretKey);
        Assert.AreEqual("bucket", options.BucketName);
        CollectionAssert.AreEqual(new[] { "a", "b" }, options.RootDirectorySegments);
        Assert.IsFalse(options.UseSSL);
        Assert.IsFalse(options.AllowInsecureHttp);
        Assert.IsFalse(options.CreateBucketIfMissing);
        Assert.IsNull(options.Region);
    }
}
