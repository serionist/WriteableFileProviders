using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Minio.Tests;

/// <summary>What only the MinIO provider does, on MinIO: stored configurations keep working, and the server's region is found.</summary>
[TestClass]
public sealed class MinioEmulatorTests
{
    private static async Task<string> ReadAllAsync(IWriteableFileProvider provider, ProviderPath path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [TestMethod]
    public async Task StoredConfiguration_KeepsWorking()
    {
        // The shape the 2026 provider (on the Minio SDK) wrote, read back by the serializer: host and port, no scheme, keys.
        var endpoint = S3Emulators.Minio;
        var root = "stored-" + Guid.NewGuid().ToString("N")[..8];
        var json = new JsonObject
        {
            ["HostAndPort"] = endpoint.HostAndPort,
            ["UseSSL"] = false,
            ["AccessKey"] = endpoint.AccessKey,
            ["SecretKey"] = endpoint.SecretKey,
            ["BucketName"] = endpoint.Bucket,
            ["RootDirectoryPath"] = root + "/",
            ["RootDirectorySegments"] = new JsonArray(root),
            ["AllowInsecureHttp"] = !endpoint.IsLoopback,
            ["Type"] = "WriteableMinioFileProvider",
        };
        using var provider = WriteableFileProviderSerializer.FromJson(json);
        try
        {
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("stored"));
            Assert.AreEqual("stored", await ReadAllAsync(provider, "f.txt"));
            // The download URL MinIO could not give before 2027 (F45), checked by the server's own signature validation.
            var url = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) });
            using var http = new HttpClient();
            Assert.AreEqual("stored", await http.GetStringAsync(url.Url));
        }
        finally
        {
            await provider.DeleteAsync("f.txt");
        }
    }

    [TestMethod]
    public async Task SiteRegion_IsFoundOrMustBeRight()
    {
        // A MinIO with MINIO_SITE_REGION refuses every request signed for another region.
        var endpoint = S3Emulators.MinioInEuCentral1;
        var root = "region-" + Guid.NewGuid().ToString("N")[..8];
        MinioFileProviderOptions Options(string? region) => new(root, endpoint.HostAndPort, endpoint.AccessKey, endpoint.SecretKey, endpoint.Bucket)
        {
            Region = region,
            AllowInsecureHttp = !endpoint.IsLoopback,
        };

        using var discovered = new WriteableMinioFileProvider(Options(null));
        await discovered.UpdateFileAsync("f.txt", TestStreams.Text("found"));
        using var named = new WriteableMinioFileProvider(Options("eu-central-1"));
        Assert.AreEqual("found", await ReadAllAsync(named, "f.txt"));
        using var wrong = new WriteableMinioFileProvider(Options("us-east-1"));
        var e = await Assert.ThrowsExactlyAsync<AmazonS3Exception>(() => wrong.ReadFileAsync("f.txt"));
        Assert.AreEqual("AuthorizationHeaderMalformed", e.ErrorCode);
        Assert.IsTrue(await discovered.DeleteAsync("f.txt"));
    }
}
