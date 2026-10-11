using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3;

namespace WriteableFileProviders.AmazonS3.Tests;

/// <summary>The options, their validation and the client configuration they stand for; no network.</summary>
[TestClass]
public sealed class AmazonS3OptionsTests
{
    private static AmazonS3FileProviderOptions Valid() => new("root", "key", "secret", "bucket", "eu-west-1");

    [TestMethod]
    public void Construction_DoesNoNetworkIO()
    {
        // An endpoint nobody listens on: building and disposing the provider must not reach for it.
        var options = new AmazonS3FileProviderOptions("root", "key", "secret", "bucket", (string?)null) { ServiceUrl = "http://127.0.0.1:1", ForcePathStyle = true };
        using var provider = new WriteableAmazonS3FileProvider(options);
        Assert.AreSame(options, provider.Options);
        using var defaultChain = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "b", BucketRegion = "us-east-1", UseDefaultCredentials = true });
    }

    [TestMethod]
    public void Options_AreValidated()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableAmazonS3FileProvider(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableAmazonS3FileProvider(Valid(), (IAmazonS3)null!));
        foreach (var (options, message) in new (AmazonS3FileProviderOptions, string)[]
        {
            (new("root", "key", "secret", null, "eu-west-1"), "BucketName"),
            (new("root", "key", "secret", "bucket", (string?)null), "BucketRegion"),
            (new("root", "key", "secret", "bucket", "eu-west-1") { ServiceUrl = "ftp://host" }, "ServiceUrl"),
            (new("root", "key", "secret", "bucket", "eu-west-1") { ServiceUrl = "not a url" }, "ServiceUrl"),
            (new("root", "key", "secret", "bucket", "eu-west-1") { UseDefaultCredentials = true }, "UseDefaultCredentials"),
            (new() { BucketName = "bucket", BucketRegion = "eu-west-1" }, "AccessKey"),
            (new() { BucketName = "bucket", BucketRegion = "eu-west-1", AccessKey = "key" }, "SecretKey"),
        })
        {
            var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(options));
            StringAssert.Contains(e.Message, message);
            Assert.AreEqual("options", e.ParamName);
        }
        // A client given by the caller brings its own region and credentials.
        using var client = new AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1);
        using var provider = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "bucket" }, client);
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions(), client));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableAmazonS3FileProvider(Valid(), (AWSCredentials)null!));
    }

    [TestMethod]
    public void Constructors_StoreRegionSystemNamesAndAnonymity()
    {
        // S3Region.USEast1 and EUWest1 carry S3's old names "" and "EU"; 2026 stored those, which AWS cannot sign for.
        Assert.AreEqual("", S3Region.USEast1.Value);
        Assert.AreEqual("us-east-1", new AmazonS3FileProviderOptions("r", "k", "s", "b", S3Region.USEast1).BucketRegion);
        Assert.AreEqual("eu-west-1", new AmazonS3FileProviderOptions("r", "k", "s", "b", S3Region.EUWest1).BucketRegion);
        Assert.AreEqual("us-west-2", new AmazonS3FileProviderOptions("r", "b", S3Region.USWest2).BucketRegion);
        Assert.IsNull(new AmazonS3FileProviderOptions("r", "b", (S3Region?)null).BucketRegion);

        var keyed = new AmazonS3FileProviderOptions("r", "k", "s", "b", "eu-west-1");
        Assert.IsFalse(keyed.IsAnonymus);
        Assert.AreEqual(S3StorageClass.Standard, keyed.StorageClassForNewFiles);
        var anonymous = new AmazonS3FileProviderOptions("r", "b", "eu-west-1", S3StorageClass.StandardInfrequentAccess);
        Assert.IsTrue(anonymous.IsAnonymus);
        Assert.AreEqual(S3StorageClass.StandardInfrequentAccess, anonymous.StorageClassForNewFiles);
        CollectionAssert.AreEqual(new[] { "r" }, anonymous.RootDirectorySegments);
    }

    [TestMethod]
    public void NewOptions_AreWrittenOnlyWhenSet()
    {
        // A configuration that uses none of the 2027 options keeps its 2026 shape.
        string[] added = ["ServiceUrl", "ForcePathStyle", "UseDefaultCredentials", "CreateBucketIfMissing", "AllowInsecureHttp"];
        using (var plain = new WriteableAmazonS3FileProvider(Valid()))
        {
            var json = plain.ToJson();
            Assert.IsFalse(added.Any(json.ContainsKey), json.ToJsonString());
        }
        using var set = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions
        {
            BucketName = "bucket",
            ServiceUrl = "http://localhost:9000",
            ForcePathStyle = true,
            UseDefaultCredentials = true,
            CreateBucketIfMissing = true,
            AllowInsecureHttp = true,
        });
        var written = set.ToJson();
        Assert.IsTrue(added.All(written.ContainsKey), written.ToJsonString());
        using var read = (WriteableAmazonS3FileProvider)WriteableFileProviderSerializer.FromJson(written);
        Assert.AreEqual("http://localhost:9000", read.Options.ServiceUrl);
        Assert.IsTrue(read.Options.ForcePathStyle && read.Options.UseDefaultCredentials && read.Options.CreateBucketIfMissing && read.Options.AllowInsecureHttp);
    }

    [TestMethod]
    [DataRow("http://minio.internal:9000")]
    [DataRow("http://10.0.0.5:9000")]
    [DataRow("HTTP://S3.EXAMPLE.COM")]
    public void PlainHttp_TowardAHostThatIsNotLoopback_NeedsAllowInsecureHttp(string endpoint)
    {
        // Files, session tokens and presigned URLs would cross the network in clear text, as MinIO's provider refuses.
        var options = new AmazonS3FileProviderOptions("root", "key", "secret", "bucket", "us-east-1") { ServiceUrl = endpoint };
        var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(options));
        Assert.AreEqual("options", e.ParamName);
        StringAssert.Contains(e.Message, "AllowInsecureHttp");
        StringAssert.Contains(e.Message, new Uri(endpoint).Host);
        options.AllowInsecureHttp = true;
        using (new WriteableAmazonS3FileProvider(options))
        {
        }

        // A client given to the provider is held to the same rule, through its own endpoint.
        using var client = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        var bare = new AmazonS3FileProviderOptions { BucketName = "bucket" };
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(bare, client)).Message, "AllowInsecureHttp");
        bare.AllowInsecureHttp = true;
        using (new WriteableAmazonS3FileProvider(bare, client))
        {
        }
    }

    [TestMethod]
    [DataRow("http://localhost:9000")]
    [DataRow("http://127.0.0.1:9000")]
    [DataRow("http://[::1]:9000")]
    [DataRow("https://minio.internal:9000")]
    public void PlainHttp_ToLoopbackOrHttps_NeedsNothing(string endpoint)
    {
        using (new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions("root", "key", "secret", "bucket", "us-east-1") { ServiceUrl = endpoint }))
        {
        }
        using var client = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        using (new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "bucket" }, client))
        {
        }
    }

    [TestMethod]
    public void PlainHttp_ToAwsThroughAGivenClient_NeedsAllowInsecureHttp()
    {
        using var client = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { RegionEndpoint = RegionEndpoint.EUWest1, UseHttp = true });
        Assert.ThrowsExactly<ArgumentException>(() => new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "bucket" }, client));
        using (new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "bucket", AllowInsecureHttp = true }, client))
        {
        }
    }

    [TestMethod]
    public void Conditions_AreLeftToTheStoreOnlyOnAws()
    {
        using (var aws = new WriteableAmazonS3FileProvider(Valid()))
            Assert.IsTrue(aws.Core.ChecksAtomically);
        using (var endpoint = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions("r", "k", "s", "b", "us-east-1") { ServiceUrl = "http://localhost:9000" }))
            Assert.IsFalse(endpoint.Core.ChecksAtomically, "An S3-compatible server may ignore a condition");
        using var awsClient = new AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.EUWest1);
        using (var given = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "b" }, awsClient))
            Assert.IsTrue(given.Core.ChecksAtomically);
        using var endpointClient = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost:9000" });
        using (var given = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = "b" }, endpointClient))
            Assert.IsFalse(given.Core.ChecksAtomically);
    }

    [TestMethod]
    public void Provider_IsSealed() =>
        // A custom provider derives from WriteableFileProvider<TOptions>, not from a provider.
        Assert.IsTrue(typeof(WriteableAmazonS3FileProvider).IsSealed);
}
