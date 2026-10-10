using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Minio.Tests;

/// <summary>
/// The contract against a MinIO server you own. Runs only with <c>WFP_LIVE=1</c> and <c>WFP_MINIO_ENDPOINT</c>
/// (<c>host:port</c>), <c>WFP_MINIO_ACCESS_KEY</c>, <c>WFP_MINIO_SECRET_KEY</c> and <c>WFP_MINIO_BUCKET</c>; HTTPS unless
/// <c>WFP_MINIO_USE_SSL</c> is <c>false</c>, and <c>WFP_MINIO_REGION</c> when the server has a site region. Each test
/// works under its own folder of <c>wfp-live-…/</c> and deletes it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class MinioLiveTests : ProviderConformanceTests
{
    private readonly string _base = "wfp-live-" + Guid.NewGuid().ToString("N")[..8];

    protected override bool KeepsContentType => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    // Above the 8 MiB part size, so a large write goes in parts.
    protected override long LargeFileSize => 20L << 20;

    protected override IWriteableFileProvider CreateProvider(string? root) => Create(LiveSettings.Get("WFP_MINIO_BUCKET"), root is null ? _base : $"{_base}/{root}");

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() => Create("wfp-missing-" + Guid.NewGuid().ToString("N")[..12], _base);

    private static WriteableMinioFileProvider Create(string? bucket, string root)
    {
        LiveSettings.Require("WFP_MINIO_ENDPOINT", "WFP_MINIO_ACCESS_KEY", "WFP_MINIO_SECRET_KEY", "WFP_MINIO_BUCKET");
        var useSsl = !string.Equals(LiveSettings.Get("WFP_MINIO_USE_SSL"), "false", StringComparison.OrdinalIgnoreCase);
        return new WriteableMinioFileProvider(new MinioFileProviderOptions(root, LiveSettings.Get("WFP_MINIO_ENDPOINT"),
            LiveSettings.Get("WFP_MINIO_ACCESS_KEY"), LiveSettings.Get("WFP_MINIO_SECRET_KEY"), bucket)
        {
            UseSSL = useSsl,
            AllowInsecureHttp = !useSsl,
            Region = LiveSettings.Get("WFP_MINIO_REGION"),
        });
    }
}
