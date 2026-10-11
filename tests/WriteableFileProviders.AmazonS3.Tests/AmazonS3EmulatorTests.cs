using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AmazonS3.Tests;

/// <summary>What only the Amazon S3 provider does, on S3Mock: version reads, and the storage class of new files.</summary>
[TestClass]
public sealed class AmazonS3EmulatorTests
{
    private static S3Endpoint Endpoint => S3Emulators.S3Mock;

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }

    [TestMethod]
    public async Task ReadFileVersion_ReadsAnOlderVersionUnderTheRoot()
    {
        var bucket = await Endpoint.CreateBucketAsync();
        try
        {
            using var client = Endpoint.CreateClient();
            await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            });
            using var provider = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions("tenant", Endpoint.AccessKey, Endpoint.SecretKey, bucket, "us-east-1")
            {
                ServiceUrl = Endpoint.ServiceUrl,
                ForcePathStyle = true,
                AllowInsecureHttp = !Endpoint.IsLoopback,
            });
            await provider.UpdateFileAsync("dir/f.txt", TestStreams.Text("one"));
            await provider.UpdateFileAsync("dir/f.txt", TestStreams.Text("two"));
            var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = "tenant/dir/f.txt" });
            var oldest = versions.Versions!.Where(v => v.Key == "tenant/dir/f.txt").OrderBy(v => v.LastModified).ThenBy(v => v.IsLatest == true).First();

            Assert.AreEqual("one", await ReadAllAsync(await provider.ReadFileVersionAsync(oldest.VersionId, "dir/f.txt")));
            Assert.AreEqual("two", await ReadAllAsync(await provider.ReadFileVersionAsync(null, "dir/f.txt")));
            Assert.AreEqual("two", await ReadAllAsync(await provider.ReadFileVersionAsync("", new[] { "dir", "f.txt" })));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileVersionAsync(null, "dir/missing.txt"));
            // The version id never reaches outside the root: the path is validated like every other.
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileVersionAsync(oldest.VersionId, "../tenant/dir/f.txt"));
        }
        finally
        {
            await DeleteVersionedBucketAsync(bucket);
        }
    }

    [TestMethod]
    public async Task StorageClass_StoredFromTheSerializedOptions()
    {
        // The object form 2026 wrote ({"Value":...}, F78) and the string configuration gives both reach the store.
        var root = "sc-" + Guid.NewGuid().ToString("N")[..8];
        var json = new JsonObject
        {
            ["IsAnonymus"] = false,
            ["AccessKey"] = Endpoint.AccessKey,
            ["SecretKey"] = Endpoint.SecretKey,
            ["BucketName"] = Endpoint.Bucket,
            ["BucketRegion"] = "us-east-1",
            ["StorageClassForNewFiles"] = new JsonObject { ["Value"] = "STANDARD_IA" },
            ["RootDirectoryPath"] = root + "/",
            ["ServiceUrl"] = Endpoint.ServiceUrl,
            ["ForcePathStyle"] = true,
            ["AllowInsecureHttp"] = !Endpoint.IsLoopback,
            ["Type"] = "WriteableAmazonS3FileProvider",
        };
        using var fromJson = WriteableFileProviderSerializer.FromJson(json);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(json.Where(p => p.Value is JsonValue)
            .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value!.ToString()))
            .Append(new("StorageClassForNewFiles", "GLACIER_IR"))).Build();
        using var fromConfiguration = WriteableFileProviderSerializer.FromConfiguration(configuration);
        using var client = Endpoint.CreateClient();
        try
        {
            await fromJson.UpdateFileAsync("a.txt", TestStreams.Text("a"));
            await fromJson.CopyAsync("a.txt", "copy.txt");
            await fromConfiguration.UpdateFileAsync("b.txt", TestStreams.Text("b"));
            foreach (var (key, expected) in new[] { ("a.txt", "STANDARD_IA"), ("copy.txt", "STANDARD_IA"), ("b.txt", "GLACIER_IR") })
            {
                var head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = Endpoint.Bucket, Key = $"{root}/{key}" });
                Assert.AreEqual(expected, head.StorageClass?.Value, key);
            }
        }
        finally
        {
            await fromJson.DeleteAsync("a.txt");
            await fromJson.DeleteAsync("copy.txt");
            await fromJson.DeleteAsync("b.txt");
        }
    }

    private static async Task DeleteVersionedBucketAsync(string bucket)
    {
        using var client = Endpoint.CreateClient();
        try
        {
            var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
            foreach (var version in versions.Versions ?? [])
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = version.Key, VersionId = version.VersionId });
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception)
        {
            // Best effort: the container goes when the run ends.
        }
    }
}
