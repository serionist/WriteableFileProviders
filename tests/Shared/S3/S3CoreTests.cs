using Amazon;
using Amazon.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.S3.Testing;

/// <summary>The S3 core's helpers that need no server: region names, the client configuration, the download name.</summary>
[TestClass]
public sealed class S3CoreTests
{
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

    [TestMethod]
    public void IsNotFound_MeansAMissingKeyOrVersionOnly()
    {
        Assert.IsTrue(S3Core.IsNotFound(new Amazon.S3.AmazonS3Exception("x") { ErrorCode = "NoSuchKey" }));
        Assert.IsTrue(S3Core.IsNotFound(new Amazon.S3.AmazonS3Exception("x") { ErrorCode = "NoSuchVersion" }));
        Assert.IsFalse(S3Core.IsNotFound(new Amazon.S3.AmazonS3Exception("x") { ErrorCode = "NoSuchBucket" }));
        Assert.IsFalse(S3Core.IsNotFound(new IOException("x")));
    }
}
