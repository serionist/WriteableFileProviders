using Amazon.S3;
using System.Text.Json.Serialization;
using WriteableFileProviders.S3;

namespace WriteableFileProviders.AmazonS3;

/// <summary>
/// The options of <see cref="WriteableAmazonS3FileProvider"/>: the bucket, how to reach it, and how to sign in. The
/// property names are the configuration shape consumers store (AGENTS.md, one-way doors).
/// </summary>
/// <remarks>
/// Sign-in: <see cref="UseDefaultCredentials"/> (the AWS SDK's default credential chain; it cannot be combined with
/// <see cref="AccessKey"/>), else <see cref="IsAnonymus"/> (unsigned requests, ignoring any <see cref="AccessKey"/>),
/// else <see cref="AccessKey"/> and <see cref="SecretKey"/>. A configuration with none of them, or with
/// <see cref="UseDefaultCredentials"/> and <see cref="AccessKey"/> both set, is refused when the provider is built.
/// </remarks>
public sealed class AmazonS3FileProviderOptions : FileProviderOptions
{
    /// <summary>Creates options that sign in with an access key, or anonymously when <paramref name="accessKey"/> is empty.</summary>
    /// <param name="rootDirectory">The root folder in the bucket, such as <c>tenants/acme</c>; null for the bucket's root.</param>
    /// <param name="accessKey">The access key id; null or empty for anonymous access.</param>
    /// <param name="secretKey">The secret access key.</param>
    /// <param name="bucketName">The bucket.</param>
    /// <param name="bucketRegion">The bucket's region, such as <c>eu-west-1</c>.</param>
    /// <param name="storageClassForNewFiles">The storage class of new files; <see cref="S3StorageClass.Standard"/> when null.</param>
    public AmazonS3FileProviderOptions(string? rootDirectory, string? accessKey, string? secretKey, string? bucketName, string? bucketRegion,
        S3StorageClass? storageClassForNewFiles = null)
    {
        RootDirectoryPath = rootDirectory;
        IsAnonymus = string.IsNullOrEmpty(accessKey);
        AccessKey = accessKey;
        SecretKey = secretKey;
        BucketName = bucketName;
        BucketRegion = bucketRegion;
        StorageClassForNewFiles = storageClassForNewFiles ?? S3StorageClass.Standard;
    }

    /// <summary>Creates options that sign in with an access key, or anonymously when <paramref name="accessKey"/> is empty.</summary>
    /// <param name="rootDirectory">The root folder in the bucket; null for the bucket's root.</param>
    /// <param name="accessKey">The access key id; null or empty for anonymous access.</param>
    /// <param name="secretKey">The secret access key.</param>
    /// <param name="bucketName">The bucket.</param>
    /// <param name="bucketRegion">
    /// The bucket's region. <see cref="S3Region.USEast1"/> and <see cref="S3Region.EUWest1"/>, whose values are S3's old
    /// names <c>""</c> and <c>EU</c>, are stored as <c>us-east-1</c> and <c>eu-west-1</c>.
    /// </param>
    /// <param name="storageClassForNewFiles">The storage class of new files; <see cref="S3StorageClass.Standard"/> when null.</param>
    public AmazonS3FileProviderOptions(string? rootDirectory, string? accessKey, string? secretKey, string? bucketName, S3Region? bucketRegion,
        S3StorageClass? storageClassForNewFiles = null)
        : this(rootDirectory, accessKey, secretKey, bucketName, RegionName(bucketRegion), storageClassForNewFiles)
    {
    }

    /// <summary>Creates options for anonymous access.</summary>
    /// <param name="rootDirectory">The root folder in the bucket; null for the bucket's root.</param>
    /// <param name="bucketName">The bucket.</param>
    /// <param name="bucketRegion">The bucket's region, such as <c>eu-west-1</c>.</param>
    /// <param name="storageClassForNewFiles">The storage class of new files; <see cref="S3StorageClass.Standard"/> when null.</param>
    public AmazonS3FileProviderOptions(string? rootDirectory, string? bucketName, string? bucketRegion, S3StorageClass? storageClassForNewFiles = null)
        : this(rootDirectory, null, null, bucketName, bucketRegion, storageClassForNewFiles)
    {
    }

    /// <summary>Creates options for anonymous access.</summary>
    /// <param name="rootDirectory">The root folder in the bucket; null for the bucket's root.</param>
    /// <param name="bucketName">The bucket.</param>
    /// <param name="bucketRegion">The bucket's region, stored as in the constructor that takes an access key.</param>
    /// <param name="storageClassForNewFiles">The storage class of new files; <see cref="S3StorageClass.Standard"/> when null.</param>
    public AmazonS3FileProviderOptions(string? rootDirectory, string? bucketName, S3Region? bucketRegion, S3StorageClass? storageClassForNewFiles = null)
        : this(rootDirectory, null, null, bucketName, RegionName(bucketRegion), storageClassForNewFiles)
    {
    }

    /// <summary>Creates empty options, for configuration binding.</summary>
    public AmazonS3FileProviderOptions()
    {
    }

    /// <summary>
    /// Sends unsigned requests, for a public bucket; such a provider cannot presign URLs. The constructors that take no
    /// access key set it. <see cref="UseDefaultCredentials"/> takes precedence.
    /// </summary>
    public bool IsAnonymus { get; set; }

    /// <summary>The access key id. It needs <see cref="SecretKey"/>.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The secret access key. A secret: keep it out of logs and out of source control.</summary>
    public string? SecretKey { get; set; }

    /// <summary>The bucket. Required.</summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// The bucket's region, such as <c>eu-west-1</c>. Required unless <see cref="ServiceUrl"/> is set, where it is the
    /// signing region (us-east-1 when empty). S3's old names, <c>""</c> or <c>US</c> for us-east-1 and <c>EU</c> for
    /// eu-west-1, read as those regions.
    /// </summary>
    public string? BucketRegion { get; set; }

    /// <summary>
    /// The storage class of new files, copies included; <see cref="S3StorageClass.Standard"/> by default. Configuration
    /// gives it by name, such as <c>STANDARD_IA</c>; null sends none, which S3 stores as STANDARD.
    /// </summary>
    public S3StorageClass? StorageClassForNewFiles { get; set; } = S3StorageClass.Standard;

    /// <summary>
    /// The endpoint of an S3-compatible server, such as <c>http://localhost:9000</c>, instead of AWS. Requests then
    /// carry checksums only where S3 requires them, presigned URLs use the endpoint's scheme, and the provider checks
    /// conditions before a write as well as sending them, since a server behind the endpoint may ignore some; for the
    /// same reason it issues no native create URL there (<see cref="SignedUrlAccess.Create"/> throws
    /// <see cref="NotSupportedException"/>). Plain <c>http</c> toward a host that is not loopback needs
    /// <see cref="AllowInsecureHttp"/>. An endpoint that is one of S3's own host names (under <c>amazonaws.com</c> or
    /// <c>amazonaws.com.cn</c>, with a label <c>s3</c> or <c>s3-fips</c>, such as <c>s3.eu-west-1.amazonaws.com</c>,
    /// <c>s3-fips.us-east-1.amazonaws.com</c>, a dual-stack or a VPC endpoint; not the legacy dashed names such as
    /// <c>s3-eu-west-1</c> or <c>s3-external-1</c>) is still S3 for the rest: requests accept requester-pays charges,
    /// and a bucket the provider creates gets the region as its location. New in 2027.0.0.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
#pragma warning disable CA1056 // Configuration binds strings; the provider checks it is an absolute http or https URI.
    public string? ServiceUrl { get; set; }
#pragma warning restore CA1056

    /// <summary>
    /// Allows a <see cref="ServiceUrl"/> with plain <c>http</c> toward a host that is not loopback, such as a server on
    /// a private network: files, session tokens and presigned URLs then travel in clear text. Off by default, so such
    /// an endpoint throws <see cref="ArgumentException"/> when the provider is built; the same holds for a client given
    /// to the provider whose own endpoint is plain <c>http</c> toward such a host. New in 2027.0.0.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Addresses the bucket in the path (<c>host/bucket/key</c>) rather than in the host name (<c>bucket.host/key</c>), as
    /// most S3-compatible servers need. New in 2027.0.0.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// Signs in through the AWS SDK's default credential chain (environment variables, profiles, web identity, ECS, EC2
    /// instance metadata), resolved on first use, instead of <see cref="AccessKey"/> or <see cref="IsAnonymus"/>. Cannot
    /// be combined with <see cref="AccessKey"/>. New in 2027.0.0.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseDefaultCredentials { get; set; }

    /// <summary>
    /// Creates the bucket on first use when it does not exist. When false (the default), every member throws
    /// <see cref="InvalidOperationException"/> naming the bucket. New in 2027.0.0.
    /// </summary>
    /// <remarks>Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CreateBucketIfMissing { get; set; }

    private static string? RegionName(S3Region? region) => region is null ? null : S3Core.NormalizeRegion(region.Value);
}
