using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3;
using WriteableFileProviders.S3.Testing;

namespace WriteableFileProviders.AmazonS3.Tests;

/// <summary>The S3 core's behaviour through the Amazon S3 provider.</summary>
public abstract class AmazonS3BehaviourTests : S3BehaviourTests
{
    internal override IWriteableFileProvider CreateProvider(string? root, string? bucket = null, bool createBucketIfMissing = false) =>
        new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions(root, Endpoint.AccessKey, Endpoint.SecretKey, bucket ?? Endpoint.Bucket, "us-east-1")
        {
            ServiceUrl = Endpoint.ServiceUrl,
            ForcePathStyle = true,
            CreateBucketIfMissing = createBucketIfMissing,
        });

    internal override IWriteableFileProvider CreateProvider(IAmazonS3 client, string? root) =>
        new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions { BucketName = Endpoint.Bucket, RootDirectoryPath = root }, client);

    internal override S3Core CoreOf(IWriteableFileProvider provider) => ((WriteableAmazonS3FileProvider)provider).Core;
}

/// <summary>On S3Mock 5.2.3.</summary>
[TestClass]
public sealed class AmazonS3OnS3MockBehaviourTests : AmazonS3BehaviourTests
{
    internal override S3Endpoint Endpoint => S3Emulators.S3Mock;

    protected override bool KeepsContentOnAbort => false;
}

/// <summary>On MinIO.</summary>
[TestClass]
public sealed class AmazonS3OnMinioBehaviourTests : AmazonS3BehaviourTests
{
    internal override S3Endpoint Endpoint => S3Emulators.Minio;

    protected override bool ValidatesSignatures => true;
}
