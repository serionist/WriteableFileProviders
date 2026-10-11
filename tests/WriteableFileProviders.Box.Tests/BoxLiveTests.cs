using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using FileFull = Box.Sdk.Gen.Schemas.FileFull;
using Files = Box.Sdk.Gen.Schemas.Files;
using Item = Box.Sdk.Gen.Schemas.Item;
using UploadPart = Box.Sdk.Gen.Schemas.UploadPart;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;
using Task = System.Threading.Tasks.Task;

// Box's digests are SHA-1: the protocol asks for them, as integrity checks.
#pragma warning disable CA5350

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// The conformance suite against a real Box app, as the provider ships. Runs only with <c>WFP_LIVE=1</c> and a Box app
/// (<see cref="BoxLive"/>); each test works in its own folder under the root folder, which it deletes (Box keeps it in
/// the trash).
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class BoxLiveTests : ProviderConformanceTests
{
    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableBoxFileProvider.DownloadUrlLifetime;

    protected override IWriteableFileProvider CreateProvider(string? root) => new WriteableBoxFileProvider(BoxLive.Options(root));
}

/// <summary>
/// The conformance suite against a real Box app with the <c>If-Match</c> header on: if it passes, Box matches the quoted
/// ETag the SDK sends, on a version upload and on an upload session's commit (the 100 MB test), and the header can be
/// turned on for good.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class BoxLiveAtomicTests : ProviderConformanceTests
{
    protected override bool HasRealFolders => true;

    protected override bool IsCaseInsensitive => true;

    protected override bool SupportsSignedUrls(SignedUrlAccess access) => access == SignedUrlAccess.Read;

    protected override TimeSpan? FixedSignedUrlLifetime => WriteableBoxFileProvider.DownloadUrlLifetime;

    protected override IWriteableFileProvider CreateProvider(string? root) => new WriteableBoxFileProvider(BoxLive.Options(root)) { SendIfMatch = true };
}

/// <summary>
/// What the offline fake assumes and only Box can confirm (the live checklist of the Box notes), checked through the SDK
/// directly so a failure names the assumption.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class BoxLiveChecks
{
    private string _folder = "";
    private WriteableBoxFileProvider? _provider;
    private BoxClient? _client;

    private WriteableBoxFileProvider Provider => _provider!;

    private BoxClient Client => _client!;

    [TestInitialize]
    public void Start()
    {
        _folder = "wfp-live-" + Guid.NewGuid().ToString("N")[..12];
        var options = BoxLive.Options(null);
        _provider = new WriteableBoxFileProvider(options);
        var config = new JwtConfig(options.ClientId!, options.ClientSecret!, options.PublicKeyId!, WriteableBoxFileProvider.NormalizePrivateKey(options.PrivateKey!), options.Passphrase ?? "")
        {
            EnterpriseId = options.EnterpriseId,
        };
        _client = new BoxClient(new BoxJwtAuth(config));
    }

    [TestCleanup]
    public async Task End()
    {
        if (_provider is null)
            return;
        await _provider.DeleteAsync(_folder);
        await _provider.DisposeAsync();
    }

    [TestMethod]
    public async Task QuotedIfMatch_MatchesTheETag()
    {
        var file = await UploadAsync("if-match.txt", "one");
        var current = $"\"{file.Etag}\"";

        // A version upload with the current ETag, quoted as the SDK must send it, succeeds.
        var next = (await Client.Uploads.UploadFileVersionAsync(file.Id,
            new UploadFileVersionRequestBody(new UploadFileVersionRequestBodyAttributesField(file.Name!), Text("two")),
            headers: new UploadFileVersionHeaders { IfMatch = current })).Entries![0];
        Assert.AreNotEqual(file.Etag, next.Etag, "A new version changes the ETag");

        // The old ETag, quoted, fails with 412 on a version upload and on a delete.
        var upload = await Assert.ThrowsExactlyAsync<BoxApiException>(() => Client.Uploads.UploadFileVersionAsync(file.Id,
            new UploadFileVersionRequestBody(new UploadFileVersionRequestBodyAttributesField(file.Name!), Text("three")),
            headers: new UploadFileVersionHeaders { IfMatch = current }));
        Assert.AreEqual(412, upload.ResponseInfo.StatusCode);
        var delete = await Assert.ThrowsExactlyAsync<BoxApiException>(() => Client.Files.DeleteFileByIdAsync(file.Id, new DeleteFileByIdHeaders { IfMatch = current }));
        Assert.AreEqual(412, delete.ResponseInfo.StatusCode);
        Assert.AreEqual("two", await ReadAsync("if-match.txt"));
    }

    [TestMethod]
    public async Task QuotedIfMatch_OnACommit_IsChecked()
    {
        var file = await UploadAsync("commit.bin", "one");
        var stale = $"\"{file.Etag}\"";
        await Provider.UpdateFileAsync($"{_folder}/commit.bin", Text("two"));
        var current = $"\"{(await FindFileAsync("commit.bin")).Etag}\"";

        // An upload session takes 20 MB at least.
        var content = TestStreams.Bytes((20L * 1024 * 1024) + 1);
        var session = await Client.ChunkedUploads.CreateFileUploadSessionForExistingFileAsync(file.Id,
            new CreateFileUploadSessionForExistingFileRequestBody(content.Length));
        try
        {
            var partSize = (int)session.PartSize!.Value;
            var parts = new List<UploadPart>();
            for (var offset = 0; offset < content.Length; offset += partSize)
            {
                var count = Math.Min(partSize, content.Length - offset);
                var digest = "sha=" + Convert.ToBase64String(SHA1.HashData(content.AsSpan(offset, count)));
                var uploaded = await Client.ChunkedUploads.UploadFilePartAsync(session.Id!, new MemoryStream(content, offset, count, writable: false),
                    new UploadFilePartHeaders(digest, $"bytes {offset}-{offset + count - 1}/{content.Length}"));
                parts.Add(uploaded.Part!);
            }
            var body = new CreateFileUploadSessionCommitRequestBody(parts);
            var whole = "sha=" + Convert.ToBase64String(SHA1.HashData(content));

            var refused = await Assert.ThrowsExactlyAsync<BoxApiException>(() =>
                Client.ChunkedUploads.CreateFileUploadSessionCommitAsync(session.Id!, body, new CreateFileUploadSessionCommitHeaders(whole) { IfMatch = stale }));
            Assert.AreEqual(412, refused.ResponseInfo.StatusCode, "A stale quoted ETag is refused at the commit");

            Files? committed = null;
            for (var attempt = 0; attempt < 60 && committed is null; attempt++)
            {
                committed = await Client.ChunkedUploads.CreateFileUploadSessionCommitAsync(session.Id!, body, new CreateFileUploadSessionCommitHeaders(whole) { IfMatch = current });
                if (committed is null)
                    await Task.Delay(TimeSpan.FromSeconds(1));
            }
            Assert.IsNotNull(committed, "The current quoted ETag commits");
        }
        catch
        {
            await Client.ChunkedUploads.DeleteFileUploadSessionByIdAsync(session.Id!);
            throw;
        }
    }

    [TestMethod]
    public async Task DownloadUrl_HonoursRange_AndServesItsVersion()
    {
        var file = await UploadAsync("range.txt", "0123456789");
        var url = await Client.Downloads.GetDownloadFileUrlAsync(file.Id, new GetDownloadFileUrlQueryParams { Version = file.FileVersion!.Id });
        await Client.Uploads.UploadFileVersionAsync(file.Id, new UploadFileVersionRequestBody(new UploadFileVersionRequestBodyAttributesField(file.Name!), Text("ABCDEFGHIJ")));

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var response = await http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("2345", await response.Content.ReadAsStringAsync(), "The URL serves the version it was made for");
    }

    [TestMethod]
    public async Task Rename_OntoATakenName_Is409_AndACaseOnlyRenameWorks()
    {
        var a = await UploadAsync("a.txt", "a");
        await UploadAsync("b.txt", "b");

        var taken = await Assert.ThrowsExactlyAsync<BoxApiException>(() => Client.Files.UpdateFileByIdAsync(a.Id, new UpdateFileByIdRequestBody { Name = "b.txt" }));
        Assert.AreEqual(409, taken.ResponseInfo.StatusCode);
        Assert.AreEqual("item_name_in_use", taken.ResponseInfo.Code);

        var renamed = await Client.Files.UpdateFileByIdAsync(a.Id, new UpdateFileByIdRequestBody { Name = "A.txt" });
        Assert.AreEqual("A.txt", renamed.Name);
    }

    [TestMethod]
    public async Task Listing_EndsItsMarkerPagesAsTheProviderExpects()
    {
        for (var i = 0; i < 3; i++)
            await UploadAsync($"f{i}.txt", "x");
        var folderId = (await FindAsync(BoxLive.RootFolderId, _folder)).FolderMini!.Id;

        var names = new List<string>();
        await foreach (var item in ListAsync(folderId, pageSize: 2))
            names.Add(item.FileFull!.Name!);

        CollectionAssert.AreEquivalent(new[] { "f0.txt", "f1.txt", "f2.txt" }, names);
    }

    private async Task<FileFull> UploadAsync(string name, string text)
    {
        await Provider.UpdateFileAsync($"{_folder}/{name}", Text(text));
        return await FindFileAsync(name);
    }

    private async Task<FileFull> FindFileAsync(string name)
    {
        var folderId = (await FindAsync(BoxLive.RootFolderId, _folder)).FolderMini!.Id;
        return (await FindAsync(folderId, name)).FileFull!;
    }

    // The root folder may hold more than a page: walk it with markers.
    private async Task<Item> FindAsync(string folderId, string name)
    {
        await foreach (var item in ListAsync(folderId, pageSize: 1000))
        {
            if ((item.FileFull?.Name ?? item.FolderMini?.Name) == name)
                return item;
        }
        throw new AssertFailedException($"'{name}' is not in folder {folderId}.");
    }

    private async IAsyncEnumerable<Item> ListAsync(string folderId, int pageSize)
    {
        string? marker = null;
        do
        {
            var page = await Client.Folders.GetFolderItemsAsync(folderId,
                new GetFolderItemsQueryParams { Usemarker = true, Marker = marker, Limit = pageSize, Fields = ["type", "id", "name", "etag", "file_version"] });
            foreach (var item in page.Entries!)
                yield return item;
            marker = page.NextMarker;
        }
        while (!string.IsNullOrEmpty(marker));
    }

    private async Task<string> ReadAsync(string name)
    {
        await using var stream = await Provider.ReadFileAsync($"{_folder}/{name}");
        return await new StreamReader(stream).ReadToEndAsync();
    }

    private static MemoryStream Text(string text) => TestStreams.Text(text);
}

/// <summary>
/// The Box app the live suites use. Set <c>WFP_LIVE=1</c>, and either <c>WFP_BOX_CONFIG_FILE</c> (the path of the app's
/// JSON configuration, as Box's developer console downloads it) with an optional <c>WFP_BOX_ROOT_FOLDER_ID</c>, or a
/// <c>testsettings.json</c> next to this project (from <c>testsettings.sample.json</c>). Without them the live tests are
/// inconclusive.
/// </summary>
internal static class BoxLive
{
    private static readonly Lazy<(BoxFileProviderOptions? Options, string Reason)> _settings = new(Load);

    public static string RootFolderId => Options(null).RootFolderId ?? "0";

    public static BoxFileProviderOptions Options(string? root)
    {
        var (options, reason) = _settings.Value;
        if (options is null)
            Assert.Inconclusive(reason);
        return new BoxFileProviderOptions(options.ClientId, options.ClientSecret, options.EnterpriseId, options.PublicKeyId,
            options.PrivateKey, options.Passphrase, options.RootFolderId) { RootDirectoryPath = root };
    }

    private static (BoxFileProviderOptions?, string) Load()
    {
        if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1")
            return (null, "Live suites run only with WFP_LIVE=1.");
        var rootFolderId = Environment.GetEnvironmentVariable("WFP_BOX_ROOT_FOLDER_ID") is { Length: > 0 } id ? id : "0";
        if (Environment.GetEnvironmentVariable("WFP_BOX_CONFIG_FILE") is { Length: > 0 } path)
        {
            var config = JsonNode.Parse(File.ReadAllText(path))!;
            var app = config["boxAppSettings"]!;
            return (new BoxFileProviderOptions((string?)app["clientID"], (string?)app["clientSecret"], (string?)config["enterpriseID"],
                (string?)app["appAuth"]?["publicKeyID"], (string?)app["appAuth"]?["privateKey"], (string?)app["appAuth"]?["passphrase"], rootFolderId), "");
        }
        var settings = Path.Combine(AppContext.BaseDirectory, "testsettings.json");
        if (File.Exists(settings))
        {
            var json = JsonNode.Parse(File.ReadAllText(settings))!;
            string? Get(string key) => (string?)json["BoxFileProvider_" + key] is { Length: > 0 } value ? value : null;
            if (Get("ClientId") is not null)
            {
                return (new BoxFileProviderOptions(Get("ClientId"), Get("ClientSecret"), Get("EnterpriseId"), Get("PublicKeyId"),
                    Get("PrivateKey"), Get("Passphrase"), Get("RootFolderId") ?? rootFolderId), "");
            }
        }
        return (null, "No Box app: set WFP_BOX_CONFIG_FILE, or fill in testsettings.json.");
    }
}
