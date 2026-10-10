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
    [DataRow(null, null)]
    [DataRow("", "us-east-1")]
    [DataRow("US", "us-east-1")]
    [DataRow("null", "us-east-1")]
    [DataRow("EU", "eu-west-1")]
    [DataRow(" eu-central-1 ", "eu-central-1")]
    public void Regions_StoredUnderOldNamesReadAsSystemNames(string? stored, string? expected) =>
        Assert.AreEqual(expected, S3Core.NormalizeRegion(stored));

    [TestMethod]
    public void ClientConfig_ForAws()
    {
        var config = S3Core.CreateConfig(new S3CoreSettings { Bucket = "b", Region = "eu-west-1" });
        Assert.AreEqual(RegionEndpoint.EUWest1, config.RegionEndpoint);
        Assert.IsNull(config.ServiceURL);
        Assert.IsFalse(config.ForcePathStyle);
        // AWS keeps the SDK's default integrity checks.
        Assert.AreEqual(RequestChecksumCalculation.WHEN_SUPPORTED, config.RequestChecksumCalculation);
        Assert.AreEqual(ResponseChecksumValidation.WHEN_SUPPORTED, config.ResponseChecksumValidation);
        Assert.AreEqual(RegionEndpoint.USEast1, S3Core.CreateConfig(new S3CoreSettings { Bucket = "b" }).RegionEndpoint);
    }

    [TestMethod]
    public void ClientConfig_ForAnS3CompatibleServer()
    {
        var config = S3Core.CreateConfig(new S3CoreSettings { Bucket = "b", ServiceUrl = "http://localhost:9000", ForcePathStyle = true });
        StringAssert.StartsWith(config.ServiceURL, "http://localhost:9000");
        Assert.IsNull(config.RegionEndpoint);
        Assert.IsTrue(config.ForcePathStyle);
        Assert.AreEqual("us-east-1", config.AuthenticationRegion);
        Assert.AreEqual(RequestChecksumCalculation.WHEN_REQUIRED, config.RequestChecksumCalculation);
        Assert.AreEqual(ResponseChecksumValidation.WHEN_REQUIRED, config.ResponseChecksumValidation);
        Assert.AreEqual("eu-central-1", S3Core.CreateConfig(new S3CoreSettings { Bucket = "b", ServiceUrl = "https://s3.example", Region = "eu-central-1" }).AuthenticationRegion);
        Assert.AreEqual("ap-south-1", S3Core.CreateConfig(new S3CoreSettings { Bucket = "b", ServiceUrl = "https://s3.example" }, "ap-south-1").AuthenticationRegion);
    }

    [TestMethod]
    public void AttachmentDisposition_EncodesTheNameBothWays()
    {
        var value = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(S3Core.AttachmentDisposition("rép\"ort;\r\nx.csv"));
        Assert.AreEqual("attachment", value.DispositionType);
        Assert.AreEqual("rép\"ort;\r\nx.csv", value.FileNameStar);
        Assert.AreEqual("r_p_ort;__x.csv", value.FileName?.Trim('"'));
        Assert.ThrowsExactly<ArgumentNullException>(() => S3Core.AttachmentDisposition(null!));
    }
}
