using Google.Cloud.Storage.V1;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>
/// The conformance suite on fake-gcs-server, through the production path: the options, with a service account key and
/// the <see cref="GoogleCloudStorageFileProviderOptions.Endpoint"/>, so the provider builds its own client and signs
/// URLs with the key.
/// </summary>
/// <remarks>
/// No capability is turned off for the emulator. It ignores generation preconditions except on uploads, so the provider
/// also sends the generation selectors it honours, and the base class checks a move's or copy's destination first; the
/// atomic guarantees only real Cloud Storage enforces run in <see cref="GcsLiveAtomicityTests"/>.
/// </remarks>
[TestClass]
public sealed class GcsConformanceTests : ProviderConformanceTests
{
    private static readonly Lazy<string> _bucket = new(() => FakeGcs.CreateBucket("wfp-conformance"));

    protected override bool KeepsContentType => true;

    // The resumable upload sends 10 MiB chunks: at 22 MiB the large write sends three, and the failed one, which stops
    // after half the size, has sent a whole chunk before it fails.
    protected override long LargeFileSize => 22L * 1024 * 1024;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new WriteableGoogleCloudStorageFileProvider(FakeGcs.Options(_bucket.Value, root));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableGoogleCloudStorageFileProvider(FakeGcs.Options(FakeGcs.MissingBucketName()));
}

/// <summary>
/// The conformance suite on fake-gcs-server over a <see cref="StorageClient"/> the test built (D17, D23): an
/// unauthenticated client, so it cannot sign, and every signed URL throws <see cref="NotSupportedException"/>. The
/// client is shared by every provider of the class, which never disposes it.
/// </summary>
[TestClass]
public sealed class GcsClientConformanceTests : ProviderConformanceTests
{
    private static readonly Lazy<StorageClient> _client = new(() => FakeGcs.CreateClient(authenticated: false));
    private static readonly Lazy<string> _bucket = new(() => FakeGcs.CreateBucket("wfp-client"));

    protected override bool KeepsContentType => true;

    protected override long LargeFileSize => 22L * 1024 * 1024;

    [ClassCleanup]
    public static void DisposeClient()
    {
        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new WriteableGoogleCloudStorageFileProvider(_client.Value, new GoogleCloudStorageFileProviderOptions { BucketName = _bucket.Value, RootDirectoryPath = root });

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableGoogleCloudStorageFileProvider(_client.Value, new GoogleCloudStorageFileProviderOptions { BucketName = FakeGcs.MissingBucketName() });
}
