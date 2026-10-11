using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AmazonS3.Tests;

/// <summary>
/// The contract against a real S3 bucket: the SDK's default checksums on writes, parts and ranged reads, multipart
/// completion, and conditional writes checked by AWS itself. Presigned URLs are only issued here, not used; what this
/// suite cannot show is the manual checklist under "For the maintainers" in the modernization plan. Without
/// <c>WFP_LIVE=1</c> it is inconclusive; with it, it needs <c>WFP_S3_BUCKET</c>, <c>WFP_S3_REGION</c>,
/// <c>WFP_S3_ACCESS_KEY</c> and <c>WFP_S3_SECRET_KEY</c> (long-term keys, so the signed URLs live as long as asked), from
/// the environment or a <c>testsettings.json</c> next to this project (see testsettings.sample.json), and fails naming
/// any that is missing. Each test works under its own folder of <c>wfp-live-…/</c> and deletes it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class AmazonS3LiveTests : ProviderConformanceTests
{
    private readonly string _base = "wfp-live-" + Guid.NewGuid().ToString("N")[..8];

    protected override bool KeepsContentType => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    // Above the 8 MiB part size, so a large write goes in parts on AWS.
    protected override long LargeFileSize => 20L << 20;

    protected override IWriteableFileProvider CreateProvider(string? root) => Create(LiveSettings.Get("WFP_S3_BUCKET"), root is null ? _base : $"{_base}/{root}");

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() => Create("wfp-missing-" + Guid.NewGuid().ToString("N")[..12], _base);

    private static WriteableAmazonS3FileProvider Create(string? bucket, string root)
    {
        LiveSettings.Require("Amazon S3 account", "WFP_S3_BUCKET", "WFP_S3_REGION", "WFP_S3_ACCESS_KEY", "WFP_S3_SECRET_KEY");
        return new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions(root, LiveSettings.Get("WFP_S3_ACCESS_KEY"),
            LiveSettings.Get("WFP_S3_SECRET_KEY"), bucket, LiveSettings.Get("WFP_S3_REGION")));
    }
}
