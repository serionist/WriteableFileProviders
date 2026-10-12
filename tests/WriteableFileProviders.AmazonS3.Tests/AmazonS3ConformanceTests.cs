using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AmazonS3.Tests;

/// <summary>The contract on an S3-compatible server, through <see cref="AmazonS3FileProviderOptions.ServiceUrl"/>.</summary>
public abstract class AmazonS3ConformanceTests : ProviderConformanceTests
{
    // Each class works under a folder of its own, so the classes never see each other's files.
    private readonly string _base = "conf-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The server.</summary>
    internal abstract S3Endpoint Endpoint { get; }

    protected override bool KeepsContentType => true;

    // Folders are name prefixes: a file and a folder may share a name.
    protected override bool FileAndFolderMayShareAName => true;

    // Toward a named endpoint, no create URL: the server may ignore the If-None-Match that guards it (the gateway's).
    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access != SignedUrlAccess.Create;

    // Three parts of 5 MiB, the smallest S3 takes: the large-file test crosses multipart without 100 MB of traffic.
    protected override long LargeFileSize => 11L << 20;

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        Create(Endpoint.Bucket, root is null ? _base : $"{_base}/{root}");

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        Create("wfp-missing-" + Guid.NewGuid().ToString("N")[..12], _base);

    // Every request fails in the AWS SDK's HTTP seam, after the SDK's own retries of what it takes for transient.
    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure)
    {
        var provider = Create(Endpoint.Bucket, $"{_base}/{RootName}");
        provider.Core.HttpClientFactory = new InterceptingHttpClientFactory { Intercept = _ => throw failure };
        return provider;
    }

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) =>
        Endpoint.StoreRawAsync($"{_base}/{string.Join('/', names)}", text);

    private WriteableAmazonS3FileProvider Create(string bucket, string root)
    {
        var provider = new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions(root, Endpoint.AccessKey, Endpoint.SecretKey, bucket, "us-east-1")
        {
            ServiceUrl = Endpoint.ServiceUrl,
            ForcePathStyle = true,
            // On a local Docker the containers listen on loopback, where plain HTTP needs no opt-in.
            AllowInsecureHttp = !Endpoint.IsLoopback,
        });
        provider.Core.PartSize = S3Core.MinimumPartSize;
        return provider;
    }
}

/// <summary>The contract on S3Mock 5.2.3.</summary>
[TestClass]
public sealed class AmazonS3OnS3MockConformanceTests : AmazonS3ConformanceTests
{
    internal override S3Endpoint Endpoint => S3Emulators.S3Mock;

    // S3Mock 5.2.3 deletes the object when a multipart upload to its key is aborted; MinIO and AWS keep it.
    protected override bool FailedLargeWriteKeepsOldContent => false;

    // S3Mock 5.2.3 answers 404 to a multipart completion with the right If-Match; MinIO and AWS complete it.
    protected override bool SupportsLargeConditionalWrites => false;

    // S3Mock 5.2.3 ignores If-None-Match, and toward a named endpoint the provider's own check before the write is not
    // atomic, so racing creates all win there. MinIO and AWS settle the race.
    protected override bool RefusesConcurrentCreates => false;
}

/// <summary>The contract on MinIO, where the large-write and conditional cases run fully.</summary>
[TestClass]
public sealed class AmazonS3OnMinioConformanceTests : AmazonS3ConformanceTests
{
    internal override S3Endpoint Endpoint => S3Emulators.Minio;

    // MinIO lists only the file where a file and a folder share a name, and hides what is under the folder meanwhile.
    protected override bool ListsAFileAndAFolderOfOneName => false;
}
