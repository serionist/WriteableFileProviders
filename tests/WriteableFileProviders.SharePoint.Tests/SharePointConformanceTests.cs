using System.Text;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// The conformance suite against the in-memory Graph, through the public constructor that takes a caller's
/// <see cref="GraphServiceClient"/>: the SDK's own pipeline, a token for Graph's host only, and a site URL with an escaped
/// space.
/// </summary>
[TestClass]
public sealed class SharePointConformanceTests : ProviderConformanceTests
{
    private static readonly GraphFake _fake = new() { RequireGraphToken = true };
    private static readonly GraphServiceClient _client = TestGraph.Client(_fake);

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    // The service names the type from the file name and takes no hint; SharePointProviderTests checks both.
    protected override bool KeepsContentType => false;

    // Checked, not atomic, until a live run shows Graph honours If-Match on a simple upload (SharePointLiveChecks);
    // SharePointProviderTests shows the If-Match the provider sends refusing a change that lands after the check.
    protected override bool DetectsConcurrentAppends => false;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableSharePointFileProvider.DownloadUrlLifetime;

    // Over 256 KiB goes through an upload session, in five fragments.
    protected override long LargeFileSize => 1_500_000;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    protected override IWriteableFileProvider CreateProvider(string? root) => TestGraph.Provider(_client, TestGraph.Options(root));

    protected override IWriteableFileProvider? CreateProviderWithMissingContainer() =>
        TestGraph.Provider(_client, TestGraph.Options(library: "No Such Library"));

    // A Graph of its own whose every request fails in the SDK's pipeline, through a client the provider builds and owns.
    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        TestGraph.OwnProvider(new GraphFake { Intercept = _ => throw failure }, TestGraph.Options(RootName));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text)
    {
        _fake.SetFile(string.Join('/', names), Encoding.UTF8.GetBytes(text));
        return Task.FromResult(true);
    }

    [ClassCleanup]
    public static void DisposeClient() => _client.Dispose();

    [TestCleanup]
    public void NothingLeaked() => Assert.IsEmpty(_fake.Violations, string.Join(Environment.NewLine, _fake.Violations));
}

/// <summary>
/// The conformance suite against the in-memory Graph through the options constructor (its own HTTP client, a client
/// secret credential that the fake host never asks for), under a work folder that the root repeats in another case
/// (the provider drops that leading copy), and with the documented variants the other fixture leaves out: a download
/// host that ignores ranges, an upload session that lists its ranges, a copy monitor that ends with 200, and listings
/// two items a page.
/// </summary>
[TestClass]
public sealed class SharePointWorkFolderConformanceTests : ProviderConformanceTests
{
    private const string WorkFolder = "Shared/Work Folder";

    private static readonly GraphFake _fake = new()
    {
        IgnoreRanges = true,
        SessionCreateListsRanges = true,
        CopyDoneStatus = System.Net.HttpStatusCode.OK,
        CopyPollsBeforeDone = 0,
        ChildrenPageSize = 2,
    };

    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool KeepsContentType => false;

    protected override bool DetectsConcurrentAppends => false;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableSharePointFileProvider.DownloadUrlLifetime;

    protected override long LargeFileSize => 700_000;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    // The root repeats the work folder in another case: one copy is dropped, so the base is the work folder itself.
    protected override IWriteableFileProvider CreateProvider(string? root) =>
        TestGraph.OwnProvider(_fake, TestGraph.Options(root is null ? "shared/work folder" : $"Shared/Work Folder/{root}", WorkFolder));

    protected override IWriteableFileProvider? CreateProviderThatFails(Exception failure) =>
        TestGraph.OwnProvider(new GraphFake { Intercept = _ => throw failure }, TestGraph.Options($"Shared/Work Folder/{RootName}", WorkFolder));

    protected override Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text)
    {
        _fake.SetFile($"{WorkFolder}/{string.Join('/', names)}", Encoding.UTF8.GetBytes(text));
        return Task.FromResult(true);
    }

    [TestCleanup]
    public void NothingLeaked()
    {
        Assert.IsEmpty(_fake.Violations, string.Join(Environment.NewLine, _fake.Violations));
        Assert.IsFalse(_fake.Requests.Any(r => r.Header("Authorization") is not null), "No token is asked for a host that is not Graph's");
    }
}
