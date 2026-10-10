using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// The conformance suite against the in-memory Box, with the provider as it ships: built from its options (JWT sign-in
/// through the fake's token endpoint), <c>IfMatch</c> checked before the write. Large files cross the chunked upload
/// threshold, lowered for the test.
/// </summary>
[TestClass]
public sealed class BoxConformanceTests : ProviderConformanceTests
{
    private static readonly FakeBox _box = new();

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableBoxFileProvider.DownloadUrlLifetime;

    // D15 as amended: IfMatch is checked just before the write, not atomically, until a live run confirms that Box
    // matches the quoted If-Match the SDK sends. BoxAtomicConformanceTests runs this rule with the header on.
    protected override bool DetectsConcurrentAppends => false;

    protected override long LargeFileSize => BoxFixture.LargeFileSize;

    protected override IWriteableFileProvider CreateProvider(string? root) => BoxFixture.Tune(TestCredentials.Default.Provider(_box, root));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        TestCredentials.Default.Provider(_box, rootFolderId: "404404");
}

/// <summary>
/// The conformance suite against the in-memory Box, with the provider built over a ready Box client (D17) and the
/// <c>If-Match</c> header sent, so preconditions and appends are atomic: what the provider becomes once a live run
/// confirms the quoted ETag.
/// </summary>
[TestClass]
public sealed class BoxAtomicConformanceTests : ProviderConformanceTests
{
    private static readonly FakeBox _box = new();

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableBoxFileProvider.DownloadUrlLifetime;

    protected override long LargeFileSize => BoxFixture.LargeFileSize;

    protected override IWriteableFileProvider CreateProvider(string? root)
    {
        var provider = new WriteableBoxFileProvider(new BoxFileProviderOptions { RootDirectoryPath = root }, TestCredentials.Default.Client(_box))
        {
            SendIfMatch = true,
            PollDelay = TimeSpan.Zero,
        };
        return BoxFixture.Tune(provider);
    }

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableBoxFileProvider(new BoxFileProviderOptions { RootFolderId = "404404" }, TestCredentials.Default.Client(_box));
}

/// <summary>What the offline fixtures share.</summary>
internal static class BoxFixture
{
    /// <summary>Uploads from this size go through an upload session: lowered from Box's 20 MiB so the suite crosses it.</summary>
    public const long ChunkedUploadThreshold = 256 * 1024;

    /// <summary>A large file of several 128 KiB parts and a short last one.</summary>
    public const long LargeFileSize = (1024 * 1024) + 17;

    public static WriteableBoxFileProvider Tune(WriteableBoxFileProvider provider)
    {
        provider.ChunkedUploadThreshold = ChunkedUploadThreshold;
        return provider;
    }
}
