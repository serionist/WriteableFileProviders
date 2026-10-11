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

    // Folders are name prefixes: a file and a folder may share a name.
    protected override bool FileAndFolderMayShareAName => true;

    // The resumable upload sends 10 MiB chunks: at 22 MiB the large write sends three, and the failed one, which stops
    // after half the size, has sent a whole chunk before it fails.
    protected override long LargeFileSize => 22L * 1024 * 1024;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => true;

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new WriteableGoogleCloudStorageFileProvider(FakeGcs.Options(_bucket.Value, root));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableGoogleCloudStorageFileProvider(FakeGcs.Options(FakeGcs.MissingBucketName()));

    // Every request fails in the client's HTTP seam; a signed-in client, so the read URL is signed after the info.
    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = _bucket.Value, RootDirectoryPath = RootName },
            FakeGcs.CreateClient(authenticated: true, new FaultInjection { OnRequest = _ => throw failure }));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => FakeGcs.StoreRawAsync(_bucket.Value, names, text);
}

/// <summary>
/// The conformance suite on fake-gcs-server over a <see cref="StorageClient"/> the test built and passed to the public
/// constructor: an unauthenticated client, so it cannot sign, and every signed URL throws
/// <see cref="NotSupportedException"/>. The client is shared by every provider of the class, which never disposes it.
/// </summary>
[TestClass]
public sealed class GcsClientConformanceTests : ProviderConformanceTests
{
    private static readonly Lazy<StorageClient> _client = new(() => FakeGcs.CreateClient(authenticated: false));
    private static readonly Lazy<string> _bucket = new(() => FakeGcs.CreateBucket("wfp-client"));

    protected override bool KeepsContentType => true;

    // Folders are name prefixes: a file and a folder may share a name.
    protected override bool FileAndFolderMayShareAName => true;

    protected override long LargeFileSize => 22L * 1024 * 1024;

    [ClassCleanup]
    public static void DisposeClient()
    {
        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }

    protected override IWriteableFileProvider CreateProvider(string? root) =>
        new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = _bucket.Value, RootDirectoryPath = root }, _client.Value);

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = FakeGcs.MissingBucketName() }, _client.Value);

    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions { BucketName = _bucket.Value, RootDirectoryPath = RootName },
            FakeGcs.CreateClient(authenticated: false, new FaultInjection { OnRequest = _ => throw failure }));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => FakeGcs.StoreRawAsync(_bucket.Value, names, text);
}
