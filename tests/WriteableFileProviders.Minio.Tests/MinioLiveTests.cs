using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.S3.Testing;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Minio.Tests;

/// <summary>
/// The contract against a MinIO server you own, such as one older than the container the emulator tests use. Without
/// <c>WFP_LIVE=1</c> it is inconclusive; with it, it needs <c>WFP_MINIO_ENDPOINT</c> (<c>host:port</c>),
/// <c>WFP_MINIO_ACCESS_KEY</c>, <c>WFP_MINIO_SECRET_KEY</c> and <c>WFP_MINIO_BUCKET</c>, from the environment or a
/// <c>testsettings.json</c> next to this project (see testsettings.sample.json), and fails naming any that is missing.
/// HTTPS unless <c>WFP_MINIO_USE_SSL</c> is <c>false</c>, and <c>WFP_MINIO_REGION</c> when the server has a site region.
/// Each test works under its own folder of <c>wfp-live-…/</c> and deletes it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class MinioLiveTests : ProviderConformanceTests
{
    private readonly string _base = "wfp-live-" + Guid.NewGuid().ToString("N")[..8];

    protected override bool KeepsContentType => true;

    // Folders are name prefixes: a file and a folder may share a name.
    protected override bool FileAndFolderMayShareAName => true;

    // MinIO lists only the file where a file and a folder share a name, and hides what is under the folder meanwhile.
    protected override bool ListsAFileAndAFolderOfOneName => false;

    // No create URL: MinIO may ignore the If-None-Match that guards it (the gateway issues one).
    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access != SignedUrlAccess.Create;

    // Above the 8 MiB part size, so a large write goes in parts.
    protected override long LargeFileSize => 20L << 20;

    protected override IWriteableFileProvider CreateProvider(string? root) => Create(LiveSettings.Get("WFP_MINIO_BUCKET"), root is null ? _base : $"{_base}/{root}");

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() => Create("wfp-missing-" + Guid.NewGuid().ToString("N")[..12], _base);

    private static WriteableMinioFileProvider Create(string? bucket, string root)
    {
        LiveSettings.Require("MinIO server", "WFP_MINIO_ENDPOINT", "WFP_MINIO_ACCESS_KEY", "WFP_MINIO_SECRET_KEY", "WFP_MINIO_BUCKET");
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
