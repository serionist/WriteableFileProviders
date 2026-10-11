using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// What the offline fake assumes and only SharePoint can confirm: whether
/// Microsoft Graph refuses a stale <c>If-Match</c> on a simple upload (<c>PUT /content</c>) and on an upload session,
/// when the session is created and at its last fragment, writing nothing. Sent through <see cref="GraphServiceClient"/>
/// directly, signed in as the provider signs in, so a failure names the assumption rather than the provider. If all
/// three hold, the provider's conditions on uploads can be called atomic (<c>SupportsAtomicPreconditions</c>, and
/// <c>DetectsConcurrentAppends</c> in <see cref="SharePointLiveTests"/>). The account is
/// <see cref="SharePointLive"/>'s; each test works in a folder of its own under the work folder and deletes it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class SharePointLiveChecks
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    // Two fragments of 320 KiB, the multiple Graph takes.
    private const int Fragment = 320 * 1024;

    private static readonly HttpClient _http = new();
    private string _folder = "";
    private SharePointFileProviderOptions? _options;
    private WriteableSharePointFileProvider? _provider;
    private GraphServiceClient? _graph;
    private X509Certificate2? _certificate;
    private string? _driveId;

    private WriteableSharePointFileProvider Provider => _provider!;

    [TestInitialize]
    public void Start()
    {
        _folder = "wfp-checks-" + Guid.NewGuid().ToString("N")[..12];
        _options = SharePointLive.Options(null);
        _provider = new WriteableSharePointFileProvider(_options);
        _graph = new GraphServiceClient(WriteableSharePointFileProvider.CreateCredential(_options, out _certificate), [GraphScope]);
    }

    [TestCleanup]
    public async Task End()
    {
        try
        {
            if (_provider is not null)
                await _provider.DeleteAsync(_folder);
        }
        finally
        {
            _provider?.Dispose();
            _graph?.Dispose();
            _certificate?.Dispose();
        }
    }

    [TestMethod]
    public async Task StaleIfMatch_OnASimpleUpload_Is412AndWritesNothing()
    {
        var stale = await WriteTwiceAsync("simple.txt");
        var item = (await RootAsync()).ItemWithPath(PathOf("simple.txt"));
        var e = await Assert.ThrowsAsync<ApiException>(() => item.Content.PutAsync(Text("three"), c => c.Headers.Add("If-Match", stale)),
            "Graph took a stale If-Match on PUT /content");
        Assert.AreEqual(412, e.ResponseStatusCode, "A stale If-Match on PUT /content is refused with 412");
        Assert.AreEqual("two", await ReadAsync("simple.txt"), "The refused upload wrote nothing");
    }

    [TestMethod]
    public async Task StaleIfMatch_AtCreateUploadSession_Is412()
    {
        var stale = await WriteTwiceAsync("session.bin");
        var item = (await RootAsync()).ItemWithPath(PathOf("session.bin"));
        var e = await Assert.ThrowsAsync<ApiException>(() => item.CreateUploadSession.PostAsync(SessionBody(), c => c.Headers.Add("If-Match", stale)),
            "Graph opened an upload session on a stale If-Match");
        Assert.AreEqual(412, e.ResponseStatusCode, "A stale If-Match on createUploadSession is refused with 412");
        Assert.AreEqual("two", await ReadAsync("session.bin"));
    }

    [TestMethod]
    public async Task IfMatch_OfASessionWhoseFileChangesBeforeItsLastFragment_Is412AndWritesNothing()
    {
        await Provider.UpdateFileAsync($"{_folder}/last.bin", Text("one"));
        var current = (await Provider.GetFileInfoAsync($"{_folder}/last.bin")).ETag!;
        var item = (await RootAsync()).ItemWithPath(PathOf("last.bin"));
        var session = await item.CreateUploadSession.PostAsync(SessionBody(), c => c.Headers.Add("If-Match", current));
        var uploadUrl = new Uri(session!.UploadUrl!);
        var content = TestStreams.Bytes(2 * Fragment);
        try
        {
            Assert.AreEqual(HttpStatusCode.Accepted, await PutFragmentAsync(uploadUrl, content, 0), "The first fragment is taken");
            await Provider.UpdateFileAsync($"{_folder}/last.bin", Text("changed"));

            var last = await PutFragmentAsync(uploadUrl, content, Fragment);
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, last, "The last fragment of a session opened on a version that is gone is refused with 412");
            Assert.AreEqual("changed", await ReadAsync("last.bin"), "The refused session wrote nothing");
        }
        finally
        {
            // Best effort: an unfinished session expires on its own.
            try
            {
                using var cancel = new HttpRequestMessage(HttpMethod.Delete, uploadUrl);
                using var _ = await _http.SendAsync(cancel);
            }
            catch (HttpRequestException)
            {
            }
        }
    }

    // Writes "one", then "two", and returns the ETag of "one": stale from then on.
    private async Task<string> WriteTwiceAsync(string name)
    {
        await Provider.UpdateFileAsync($"{_folder}/{name}", Text("one"));
        var stale = (await Provider.GetFileInfoAsync($"{_folder}/{name}")).ETag!;
        await Provider.UpdateFileAsync($"{_folder}/{name}", Text("two"));
        Assert.AreNotEqual(stale, (await Provider.GetFileInfoAsync($"{_folder}/{name}")).ETag, "A new version changes the ETag");
        return stale;
    }

    // The library's top level, where items are addressed by path as the provider addresses them.
    private async Task<Microsoft.Graph.Drives.Item.Root.RootRequestBuilder> RootAsync() => _graph!.Drives[await DriveIdAsync()].Root;

    // A test file's path in the library: under the work folder and the test's folder.
    private string PathOf(string name) => string.Join('/', SharePointPaths.WorkFolderSegments(_options!.WorkFolderPath).Append(_folder).Append(name));

    private async Task<string> DriveIdAsync()
    {
        if (_driveId is not null)
            return _driveId;
        var site = new Uri(_options!.SiteUrl!);
        var sitePath = Uri.UnescapeDataString(site.AbsolutePath).TrimEnd('/');
        var siteId = (await _graph!.Sites[$"{site.Host}:{(sitePath.Length == 0 ? "/" : sitePath)}"].GetAsync())!.Id!;
        // Page through the site's libraries, as the provider does.
        var drives = _graph.Sites[siteId].Drives;
        for (var page = await drives.GetAsync(); page is not null;
             page = page.OdataNextLink is { } next ? await drives.WithUrl(next).GetAsync() : null)
        {
            if ((page.Value ?? []).FirstOrDefault(d => string.Equals(d.Name, _options.DocumentLibraryName, StringComparison.OrdinalIgnoreCase)) is { Id: { } id })
                return _driveId = id;
        }
        throw new AssertFailedException($"The site has no library named '{_options.DocumentLibraryName}'.");
    }

    private static CreateUploadSessionPostRequestBody SessionBody() => new()
    {
        Item = new DriveItemUploadableProperties { AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace" } },
    };

    // A fragment goes to the pre-authenticated upload URL without a token.
    private static async Task<HttpStatusCode> PutFragmentAsync(Uri uploadUrl, byte[] content, int offset)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(content, offset, Fragment) };
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + Fragment - 1, content.Length);
        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<string> ReadAsync(string name)
    {
        await using var stream = await Provider.ReadFileAsync($"{_folder}/{name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static MemoryStream Text(string text) => TestStreams.Text(text);
}
