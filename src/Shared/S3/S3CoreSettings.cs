using Amazon.Runtime;
using Amazon.S3;

namespace WriteableFileProviders.S3;

/// <summary>What <see cref="S3Core"/> needs from a provider's options, once they are validated.</summary>
internal sealed record S3CoreSettings
{
    /// <summary>The bucket.</summary>
    public required string Bucket { get; init; }

    /// <summary>A custom endpoint (MinIO, S3Mock, another S3-compatible server), or null for AWS.</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>The AWS region, or the signing region of a custom endpoint; null: us-east-1, or discovered.</summary>
    public string? Region { get; init; }

    /// <summary>Whether to ask a custom endpoint for the bucket's region when <see cref="Region"/> is null (MinIO).</summary>
    public bool DiscoverRegion { get; init; }

    /// <summary>Whether to address the bucket in the path rather than in the host name.</summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>The credentials, or null for the SDK's default credential chain, resolved on first use.</summary>
    public AWSCredentials? Credentials { get; init; }

    /// <summary>The storage class of new objects, or null to send none (MinIO refuses classes it does not know).</summary>
    public S3StorageClass? StorageClass { get; init; }

    /// <summary>Whether requests accept the charges of a requester-pays bucket.</summary>
    public bool RequesterPays { get; init; }

    /// <summary>Whether a missing bucket is created; otherwise every member throws <see cref="InvalidOperationException"/>.</summary>
    public bool CreateBucketIfMissing { get; init; }
}
