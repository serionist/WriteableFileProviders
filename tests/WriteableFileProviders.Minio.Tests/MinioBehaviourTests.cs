using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3;
using WriteableFileProviders.S3.Testing;

namespace WriteableFileProviders.Minio.Tests;

/// <summary>The S3 core's behaviour through the MinIO provider.</summary>
public abstract class MinioBehaviourTests : S3BehaviourTests
{
    internal override IWriteableFileProvider CreateProvider(string? root, string? bucket = null, bool createBucketIfMissing = false) =>
        new WriteableMinioFileProvider(new MinioFileProviderOptions(root, Endpoint.HostAndPort, Endpoint.AccessKey, Endpoint.SecretKey, bucket ?? Endpoint.Bucket)
        {
            CreateBucketIfMissing = createBucketIfMissing,
            AllowInsecureHttp = !Endpoint.IsLoopback,
        });

    internal override IWriteableFileProvider CreateProvider(IAmazonS3 client, string? root) =>
        new WriteableMinioFileProvider(new MinioFileProviderOptions { BucketName = Endpoint.Bucket, RootDirectoryPath = root, AllowInsecureHttp = !Endpoint.IsLoopback }, client);

    internal override S3Core CoreOf(IWriteableFileProvider provider) => ((WriteableMinioFileProvider)provider).Core;
}

/// <summary>On S3Mock 5.2.3.</summary>
[TestClass]
public sealed class MinioOnS3MockBehaviourTests : MinioBehaviourTests
{
    internal override S3Endpoint Endpoint => S3Emulators.S3Mock;

    protected override bool KeepsContentOnAbort => false;
}

/// <summary>On MinIO.</summary>
[TestClass]
public sealed class MinioOnMinioBehaviourTests : MinioBehaviourTests
{
    internal override S3Endpoint Endpoint => S3Emulators.Minio;

    protected override bool ValidatesSignatures => true;
}
