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

    protected override long LargeFileSize => BoxFixture.LargeFileSize;

    protected override IWriteableFileProvider CreateProvider(string? root) => BoxFixture.Tune(TestCredentials.Default.Provider(_box, root));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        TestCredentials.Default.Provider(_box, rootFolderId: "404404");

    // A Box of its own whose every request but the sign-in fails behind the SDK's network client, so each member's own
    // requests are the ones that fail.
    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        BoxFixture.Tune(TestCredentials.Default.Provider(new FakeBox { Intercept = BoxFixture.FailAllButSignIn(failure) }, RootName));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => Task.FromResult(BoxFixture.StoreRaw(_box, names, text));
}

/// <summary>
/// The conformance suite against the in-memory Box, with the provider built over a ready Box client and the
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

    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        BoxFixture.Tune(new WriteableBoxFileProvider(new BoxFileProviderOptions { RootDirectoryPath = RootName },
            TestCredentials.Default.Client(new FakeBox { Intercept = BoxFixture.FailAllButSignIn(failure) }))
        {
            SendIfMatch = true,
            PollDelay = TimeSpan.Zero,
        });

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => Task.FromResult(BoxFixture.StoreRaw(_box, names, text));
}

/// <summary>What the offline fixtures share.</summary>
internal static class BoxFixture
{
    /// <summary>An interception that fails every request with <paramref name="failure"/>, except the sign-in.</summary>
    /// <param name="failure">What each request throws.</param>
    public static Func<HttpRequestMessage, HttpResponseMessage?> FailAllButSignIn(Exception failure) =>
        request => request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal) ? null : throw failure;

    /// <summary>Uploads from this size go through an upload session: lowered from Box's 20 MiB so the suite crosses it.</summary>
    public const long ChunkedUploadThreshold = 256 * 1024;

    /// <summary>A large file of several 128 KiB parts and a short last one.</summary>
    public const long LargeFileSize = (1024 * 1024) + 17;

    public static WriteableBoxFileProvider Tune(WriteableBoxFileProvider provider)
    {
        provider.ChunkedUploadThreshold = ChunkedUploadThreshold;
        return provider;
    }

    /// <summary>Stores a file in the fake under any names, past the provider and Box's name rules, with its folders.</summary>
    /// <param name="box">The fake.</param>
    /// <param name="names">The file's names under the account root, one per level.</param>
    /// <param name="text">The content.</param>
    /// <returns>True.</returns>
    public static bool StoreRaw(FakeBox box, IReadOnlyList<string> names, string text)
    {
        var parent = FakeBox.RootId;
        var path = "";
        foreach (var name in names.Take(names.Count - 1))
        {
            path += "/" + name;
            parent = box.IdOf(path) ?? box.AddFolder(parent, name);
        }
        box.AddFile(parent, names[^1], text);
        return true;
    }
}
