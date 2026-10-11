using System.Net;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// What the conformance suite cannot reach, on Azurite: blobs other tools wrote (append blobs, page blobs, blobs without a
/// block list), Azure's block limit, races injected through the HTTP pipeline, missing containers, and signed URLs used
/// over HTTP. Requests go through <see cref="AzureHandler"/>, which refuses what Azure refuses.
/// </summary>
[TestClass]
public sealed partial class AzureBlobProviderTests
{
    private static readonly string _containerName = Azurite.NewContainerName();
    private AzureHandler _handler = new();
    private BlobContainerClient _raw = null!;
    private string _root = "";

    [TestInitialize]
    public async Task Start()
    {
        _raw = await Azurite.CreateContainerAsync(_containerName);
        _handler = new AzureHandler();
        _root = "t" + Guid.NewGuid().ToString("N")[..10];
    }

    [ClassCleanup]
    public static Task DeleteContainer() => Azurite.DeleteContainerAsync(_containerName);

    // A provider rooted at the test's own folder, over a client whose requests go through the handler.
    private WriteableAzureBlobFileProvider Provider(int retries = 2, AzureFileProviderOptions? options = null) =>
        new(Azurite.Container(_containerName, _handler, retries), options ?? new AzureFileProviderOptions { RootDirectoryPath = _root });

    private string Name(string path) => $"{_root}/{path}";

    // A blob written as the 2026 provider wrote them: an append blob, through the same pipeline, so the handler knows it.
    private async Task<AppendBlobClient> LegacyAppendBlob(string path, string content)
    {
        var blob = Azurite.Container(_containerName, _handler).GetAppendBlobClient(Name(path));
        await blob.CreateAsync(new AppendBlobCreateOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "text/x-legacy", CacheControl = "no-cache" },
            Metadata = new Dictionary<string, string> { ["origin"] = "2026" },
        });
        if (content.Length > 0)
            await blob.AppendBlockAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        return blob;
    }

    private async Task<string> ReadRaw(string path)
    {
        var download = await _raw.GetBlobClient(Name(path)).DownloadContentAsync();
        return download.Value.Content.ToString();
    }

    private static async Task<string> Read(IWriteableFileProvider provider, string path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static Task Write(IWriteableFileProvider provider, string path, string text, UpdateFileOptions? options = null) =>
        provider.UpdateFileAsync(path, TestStreams.Text(text), options);

    private static Task Append(IWriteableFileProvider provider, string path, string text) =>
        provider.UpdateFileAsync(path, TestStreams.Text(text), UpdateFileOptions.Append);

    // The commit of a block list (Put Block List), not the read of one (Get Block List).
    private static bool IsCommit(HttpRequestMessage request) => request.Method == HttpMethod.Put && AzureHandler.Comp(request) == "blocklist";

    // A logged Get Blob: a download, not a read of the block list.
    private static bool IsDownload(string logged) => logged.StartsWith("GET", StringComparison.Ordinal) && !logged.Contains("?comp=", StringComparison.Ordinal);

    private async Task<List<string>> TempBlobs()
    {
        var names = new List<string>();
        await foreach (var item in _raw.GetBlobsAsync(new GetBlobsOptions { Prefix = Name(".wfp-tmp/") }))
            names.Add(item.Name);
        return names;
    }

    private async Task<List<BlobBlock>> CommittedBlocks(string path) =>
        [.. (await _raw.GetBlockBlobClient(Name(path)).GetBlockListAsync(BlockListTypes.Committed)).Value.CommittedBlocks];

    // ----- Writes -----

    [TestMethod]
    public async Task Overwrite_StagesBlocksAndCommitsOnce()
    {
        using var provider = Provider();
        provider.BlockSize = 4;
        await Write(provider, "f.txt", "0123456789");
        Assert.AreEqual("0123456789", await ReadRaw("f.txt"));
        var blocks = await CommittedBlocks("f.txt");
        CollectionAssert.AreEqual(new long[] { 4, 4, 2 }, blocks.ConvertAll(b => b.SizeLong));
        Assert.IsTrue(blocks.TrueForAll(b => b.Name.Length == 64), "Random 64-character block ids");
        Assert.AreEqual(1, _handler.For(Name("f.txt")).Count(l => l.Contains("comp=blocklist", StringComparison.Ordinal)));
        Assert.AreEqual(0, _handler.For(Name("f.txt")).Count(l => l.EndsWith("BlockBlob", StringComparison.Ordinal)), "Never a Put Blob");
    }

    [TestMethod]
    public async Task Overwrite_FailedCommitKeepsTheOldContent()
    {
        using var provider = Provider(retries: 0);
        await Write(provider, "f.txt", "original");
        _handler.Intercept = r => IsCommit(r) ? AzureHandler.Error(HttpStatusCode.InternalServerError, "InternalError") : null;
        await Assert.ThrowsExactlyAsync<RequestFailedException>(() => Write(provider, "f.txt", "replacement"));
        _handler.Intercept = null;
        Assert.AreEqual("original", await Read(provider, "f.txt"));
    }

    [TestMethod]
    public async Task Overwrite_StoresTheContentTypeOrOctetStream()
    {
        using var provider = Provider();
        await Write(provider, "plain", "x");
        Assert.AreEqual("application/octet-stream", (await provider.GetFileInfoAsync("plain")).ContentType);
        await Write(provider, "page.html", "<p/>", new UpdateFileOptions { ContentType = "text/html" });
        Assert.AreEqual("text/html", (await provider.GetFileInfoAsync("page.html")).ContentType);
        await Write(provider, "empty.txt", "", new UpdateFileOptions { ContentType = "text/plain" });
        var empty = await provider.GetFileInfoAsync("empty.txt");
        Assert.AreEqual(0, empty.Length);
        Assert.AreEqual("text/plain", empty.ContentType);
    }

    [TestMethod]
    public async Task Overwrite_SizesBlocksToFitTheLimitWhenTheStreamCanSeek()
    {
        _handler = new AzureHandler { MaxBlocks = 4 };
        using var provider = Provider();
        provider.BlockSize = 1024;
        provider.MaxCommittedBlocks = 4;
        var bytes = TestStreams.Bytes(10_000);
        await provider.UpdateFileAsync("big.bin", new MemoryStream(bytes));
        Assert.AreEqual(4, (await CommittedBlocks("big.bin")).Count);
        CollectionAssert.AreEqual(bytes, (await _raw.GetBlobClient(Name("big.bin")).DownloadContentAsync()).Value.Content.ToArray());

        // A stream that cannot seek is read in blocks of the configured size: past the limit, nothing is written.
        var e = await Assert.ThrowsAsync<IOException>(() => provider.UpdateFileAsync("big.bin", new NonSeekableStream(TestStreams.Bytes(10_000, seed: 2))));
        StringAssert.Contains(e.Message, "4 blocks");
        CollectionAssert.AreEqual(bytes, (await _raw.GetBlobClient(Name("big.bin")).DownloadContentAsync()).Value.Content.ToArray());
    }

    [TestMethod]
    public async Task Append_ReusesTheBlockListAndKeepsWhatDescribesTheFile()
    {
        using var provider = Provider();
        await Write(provider, "log.txt", "one;", new UpdateFileOptions { ContentType = "text/plain" });
        var blob = _raw.GetBlobClient(Name("log.txt"));
        await blob.SetMetadataAsync(new Dictionary<string, string> { ["owner"] = "tests" });
        await blob.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = "text/plain", CacheControl = "max-age=60", ContentLanguage = "en" });

        await Append(provider, "log.txt", "two;");
        Assert.AreEqual("one;two;", await ReadRaw("log.txt"));
        var properties = (await blob.GetPropertiesAsync()).Value;
        Assert.AreEqual("text/plain", properties.ContentType);
        Assert.AreEqual("max-age=60", properties.CacheControl);
        Assert.AreEqual("en", properties.ContentLanguage);
        Assert.AreEqual("tests", properties.Metadata["owner"]);
        Assert.AreEqual(2, (await CommittedBlocks("log.txt")).Count, "The first block is reused, not copied");

        await provider.UpdateFileAsync("log.txt", TestStreams.Text("three;"), new UpdateFileOptions { Mode = UpdateFileMode.Append, ContentType = "text/csv" });
        Assert.AreEqual("text/csv", (await provider.GetFileInfoAsync("log.txt")).ContentType, "A content type given replaces the stored one");
        Assert.AreEqual("one;two;three;", await ReadRaw("log.txt"));
    }

    [TestMethod]
    public async Task Append_ToAMissingFileCreatesItWithTheConditions()
    {
        using var provider = Provider();
        await provider.UpdateFileAsync("new.txt", TestStreams.Text("a"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfNotExists = true });
        Assert.AreEqual("a", await ReadRaw("new.txt"));
        Assert.AreEqual("application/octet-stream", (await provider.GetFileInfoAsync("new.txt")).ContentType);
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("new.txt", TestStreams.Text("b"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfNotExists = true }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("missing.txt", TestStreams.Text("b"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfMatch = "\"0x1\"" }));
        var etag = (await provider.GetFileInfoAsync("new.txt")).ETag;
        await provider.UpdateFileAsync("new.txt", TestStreams.Text("b"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfMatch = etag });
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("new.txt", TestStreams.Text("c"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfMatch = etag }));
        Assert.AreEqual("ab", await ReadRaw("new.txt"));
    }

    [TestMethod]
    public async Task Append_ToABlobWrittenInOnePutBlobRestagesItOnTheServer()
    {
        await _raw.GetBlobClient(Name("put.txt")).UploadAsync(BinaryData.FromString("written by another tool;"));
        Assert.AreEqual(0, (await CommittedBlocks("put.txt")).Count, "Put Blob leaves no block list");
        using var provider = Provider();
        await Append(provider, "put.txt", "appended");
        Assert.AreEqual("written by another tool;appended", await ReadRaw("put.txt"));
        Assert.IsTrue((await CommittedBlocks("put.txt")).TrueForAll(b => b.Name.Length == 64));
        Assert.IsTrue(_handler.For(Name("put.txt")).Contains($"PUT {Name("put.txt")}?comp=block copy"), "Put Block From URL, no download");
        Assert.IsFalse(_handler.For(Name("put.txt")).Any(IsDownload), "Nothing was downloaded");
    }

    [TestMethod]
    public async Task Append_ToBlocksWithForeignIdsRestagesThem()
    {
        var blob = _raw.GetBlockBlobClient(Name("short-ids.txt"));
        var ids = new[] { "QUFB", "QkJC" };
        await blob.StageBlockAsync(ids[0], TestStreams.Text("aa;"));
        await blob.StageBlockAsync(ids[1], TestStreams.Text("bb;"));
        await blob.CommitBlockListAsync(ids);
        using var provider = Provider();
        await Append(provider, "short-ids.txt", "cc;");
        Assert.AreEqual("aa;bb;cc;", await ReadRaw("short-ids.txt"));
        Assert.IsTrue((await CommittedBlocks("short-ids.txt")).TrueForAll(b => b.Name.Length == 64), "One id length for every block");
    }

    [TestMethod]
    public async Task Append_PastTheBlockLimitCompactsTheFileFirst()
    {
        _handler = new AzureHandler { MaxBlocks = 4 };
        using var provider = Provider();
        provider.MaxCommittedBlocks = 4;
        provider.CopyRangeSize = 3;
        foreach (var part in new[] { "a1;", "b2;", "c3;", "d4;", "e5;", "f6;", "g7;" })
            await Append(provider, "log.txt", part);
        Assert.AreEqual("a1;b2;c3;d4;e5;f6;g7;", await ReadRaw("log.txt"));
        Assert.IsLessThanOrEqualTo(4, (await CommittedBlocks("log.txt")).Count);
    }

    [TestMethod]
    public async Task Append_WithoutASignatureForTheSourceRestagesThroughTheClient()
    {
        await _raw.GetBlobClient(Name("put.txt")).UploadAsync(BinaryData.FromString("base;"));
        // A client from a container SAS can neither sign a SAS nor get a user delegation key.
        var sas = _raw.GenerateSasUri(BlobContainerSasPermissions.All, DateTimeOffset.UtcNow.AddHours(1));
        var client = new BlobContainerClient(sas, Azurite.ClientOptions(_handler));
        using var provider = new WriteableAzureBlobFileProvider(client, new AzureFileProviderOptions { RootDirectoryPath = _root });
        await Append(provider, "put.txt", "more");
        Assert.AreEqual("base;more", await ReadRaw("put.txt"));
        Assert.IsFalse(_handler.For(Name("put.txt")).Any(l => l.EndsWith("copy", StringComparison.Ordinal)), "No server-side copy");
        Assert.IsTrue(_handler.For(Name("put.txt")).Any(IsDownload), "The ranges were downloaded");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("put.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) }));
    }

    [TestMethod]
    public async Task Append_WhenTheServiceRefusesTheCopySourceRestagesThroughTheClient()
    {
        await _raw.GetBlobClient(Name("put.txt")).UploadAsync(BinaryData.FromString("base;"));
        using var provider = Provider();
        _handler.Intercept = r => AzureHandler.Comp(r) == "block" && AzureHandler.IsCopy(r) ? AzureHandler.Error(HttpStatusCode.Forbidden, "CannotVerifyCopySource") : null;
        await Append(provider, "put.txt", "one;");
        await _raw.GetBlobClient(Name("put2.txt")).UploadAsync(BinaryData.FromString("base;"));
        await Append(provider, "put2.txt", "two;");
        Assert.AreEqual("base;one;", await ReadRaw("put.txt"));
        Assert.AreEqual("base;two;", await ReadRaw("put2.txt"));
        Assert.AreEqual(1, _handler.Log.Count(l => l.EndsWith("comp=block copy", StringComparison.Ordinal)), "Refused once, then not asked again");

        // The refusal is remembered for a while only: a permission granted since is used.
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        provider.Clock = clock;
        provider.ServerCopyRefusedUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(10);
        clock.Advance(TimeSpan.FromMinutes(11));
        _handler.Intercept = null;
        await _raw.GetBlobClient(Name("put3.txt")).UploadAsync(BinaryData.FromString("base;"));
        await Append(provider, "put3.txt", "three;");
        Assert.AreEqual("base;three;", await ReadRaw("put3.txt"));
        Assert.AreEqual(2, _handler.Log.Count(l => l.EndsWith("comp=block copy", StringComparison.Ordinal)), "Asked again after the refusal expired");
    }

    [TestMethod]
    public async Task Append_RetriesAfterAConcurrentCommit()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "start;");
        var raced = false;
        _handler.Intercept = r =>
        {
            if (raced || !IsCommit(r) || AzureHandler.BlobName(r) != Name("f.txt"))
                return null;
            raced = true;
            _raw.GetBlockBlobClient(Name("f.txt")).Upload(new MemoryStream(Encoding.UTF8.GetBytes("theirs;")));
            return null;
        };
        await Append(provider, "f.txt", "mine;");
        Assert.AreEqual("theirs;mine;", await ReadRaw("f.txt"), "The concurrent write is kept, and the append applied after it");
    }

    [TestMethod]
    public async Task Append_GivesUpAfterBoundedRetries()
    {
        using var provider = Provider();
        provider.MaxAttempts = 3;
        await Write(provider, "f.txt", "start;");
        var races = 0;
        _handler.Intercept = r =>
        {
            if (!IsCommit(r) || AzureHandler.BlobName(r) != Name("f.txt"))
                return null;
            races++;
            _raw.GetBlockBlobClient(Name("f.txt")).Upload(new MemoryStream(Encoding.UTF8.GetBytes($"theirs{races};")));
            return null;
        };
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Append(provider, "f.txt", "mine;"));
        StringAssert.Contains(e.Message, "3 attempts");
        Assert.AreEqual(3, races);
        Assert.AreEqual("theirs3;", await ReadRaw("f.txt"), "Nothing of the append landed, and nothing was lost");
    }

    [TestMethod]
    public async Task Append_WithIfMatchDoesNotRetry()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "start;");
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        var commits = 0;
        _handler.Intercept = r =>
        {
            if (IsCommit(r) && AzureHandler.BlobName(r) == Name("f.txt") && ++commits == 1)
                _raw.GetBlockBlobClient(Name("f.txt")).Upload(new MemoryStream(Encoding.UTF8.GetBytes("theirs;")));
            return null;
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("mine;"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfMatch = etag }));
        Assert.AreEqual(1, commits, "The caller's version is gone: no second attempt");
        Assert.AreEqual("theirs;", await ReadRaw("f.txt"));
    }

    // ----- Append blobs from 2026.10.10.x (P30) -----

    [TestMethod]
    public async Task LegacyAppendBlob_IsReadable()
    {
        await LegacyAppendBlob("legacy.txt", "old content");
        using var provider = Provider();
        Assert.AreEqual("old content", await Read(provider, "legacy.txt"));
        var info = await provider.GetFileInfoAsync("legacy.txt");
        Assert.AreEqual(11, info.Length);
        Assert.AreEqual("text/x-legacy", info.ContentType);
        await using var range = await provider.ReadRangeAsync("legacy.txt", 4, 3);
        Assert.AreEqual("con", await new StreamReader(range).ReadToEndAsync());
    }

    [TestMethod]
    public async Task LegacyAppendBlob_OverwriteDeletesItThenCopiesTheNewContent()
    {
        await LegacyAppendBlob("legacy.txt", "old");
        using var provider = Provider();
        provider.BlockSize = 2;
        await Write(provider, "legacy.txt", "brand new", new UpdateFileOptions { ContentType = "text/plain" });

        var properties = (await _raw.GetBlobClient(Name("legacy.txt")).GetPropertiesAsync()).Value;
        Assert.AreEqual(BlobType.Block, properties.BlobType);
        Assert.AreEqual("text/plain", properties.ContentType);
        Assert.AreEqual("brand new", await ReadRaw("legacy.txt"));
        Assert.AreEqual(0, (await TempBlobs()).Count, "The temp blob is removed");

        // The order Azure needs: no Put Blob or block list onto the append blob, a delete, then the copy.
        var requests = _handler.For(Name("legacy.txt"));
        var delete = requests.IndexOf($"DELETE {Name("legacy.txt")}");
        var copy = requests.IndexOf($"PUT {Name("legacy.txt")} copy");
        Assert.IsTrue(delete >= 0 && copy > delete, string.Join(" | ", requests));
        Assert.IsFalse(requests.Contains($"PUT {Name("legacy.txt")}?comp=blocklist"), string.Join(" | ", requests));
    }

    [TestMethod]
    public async Task LegacyAppendBlob_AppendConvertsItKeepingWhatDescribesIt()
    {
        await LegacyAppendBlob("legacy.txt", "line1;");
        using var provider = Provider();
        await Append(provider, "legacy.txt", "line2;");
        var blob = _raw.GetBlobClient(Name("legacy.txt"));
        var properties = (await blob.GetPropertiesAsync()).Value;
        Assert.AreEqual(BlobType.Block, properties.BlobType);
        Assert.AreEqual("line1;line2;", await ReadRaw("legacy.txt"));
        Assert.AreEqual("text/x-legacy", properties.ContentType);
        Assert.AreEqual("no-cache", properties.CacheControl);
        Assert.AreEqual("2026", properties.Metadata["origin"]);
        Assert.AreEqual(0, (await TempBlobs()).Count);

        // Afterwards it is a block blob like any other: the next append reuses its blocks.
        await Append(provider, "legacy.txt", "line3;");
        Assert.AreEqual("line1;line2;line3;", await ReadRaw("legacy.txt"));
        Assert.AreEqual(1, _handler.For(Name("legacy.txt")).Count(l => l.StartsWith("DELETE", StringComparison.Ordinal)), "Converted once");
    }

    [TestMethod]
    public async Task LegacyAppendBlob_ConditionsAreCheckedBeforeAnythingIsDeleted()
    {
        var legacy = await LegacyAppendBlob("legacy.txt", "old");
        var etag = (await legacy.GetPropertiesAsync()).Value.ETag;
        using var provider = Provider();
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "legacy.txt", "x", new UpdateFileOptions { IfNotExists = true }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "legacy.txt", "x", new UpdateFileOptions { IfMatch = "\"0x8D000000000000\"" }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("legacy.txt", TestStreams.Text("x"), new UpdateFileOptions { Mode = UpdateFileMode.Append, IfMatch = "\"0x8D000000000000\"" }));
        Assert.AreEqual("old", await ReadRaw("legacy.txt"));
        Assert.AreEqual(BlobType.Append, (await legacy.GetPropertiesAsync()).Value.BlobType);
        Assert.AreEqual(0, (await TempBlobs()).Count);

        await Write(provider, "legacy.txt", "matched", new UpdateFileOptions { IfMatch = etag.ToString("H") });
        Assert.AreEqual("matched", await ReadRaw("legacy.txt"));
    }

    [TestMethod]
    public async Task LegacyAppendBlob_FailedCopyKeepsTheContentInTheNamedTempBlob()
    {
        await LegacyAppendBlob("legacy.txt", "old;");
        using var provider = Provider(retries: 0);
        _handler.Intercept = r => AzureHandler.IsCopy(r) && AzureHandler.BlobName(r) == Name("legacy.txt") ? AzureHandler.Error(HttpStatusCode.InternalServerError, "InternalError") : null;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => Append(provider, "legacy.txt", "new;"));
        _handler.Intercept = null;

        var temps = await TempBlobs();
        Assert.AreEqual(1, temps.Count);
        var tempPath = temps[0][(_root.Length + 1)..];
        StringAssert.Contains(e.Message, tempPath);
        Assert.IsFalse((await provider.GetFileInfoAsync("legacy.txt")).Exists, "The append blob was deleted");
        Assert.AreEqual("old;new;", await Read(provider, tempPath), "The provider reads the kept content at the path the message names");
        CollectionAssert.DoesNotContain((await provider.GetDirectoryContentsAsync("")).Select(f => f.Name).ToList(), ".wfp-tmp", "Listings hide it");
    }

    [TestMethod]
    public async Task LegacyAppendBlob_ChangedDuringTheConversionIsReadAgain()
    {
        var legacy = await LegacyAppendBlob("legacy.txt", "old;");
        using var provider = Provider();
        var raced = false;
        _handler.Intercept = r =>
        {
            if (raced || r.Method != HttpMethod.Delete || AzureHandler.BlobName(r) != Name("legacy.txt"))
                return null;
            raced = true;
            legacy.AppendBlock(new MemoryStream(Encoding.UTF8.GetBytes("theirs;")));
            return null;
        };
        await Append(provider, "legacy.txt", "mine;");
        Assert.AreEqual("old;theirs;mine;", await ReadRaw("legacy.txt"));
        Assert.AreEqual(0, (await TempBlobs()).Count);
    }

    [TestMethod]
    public async Task PageBlob_IsReadOnly()
    {
        var page = _raw.GetPageBlobClient(Name("disk.vhd"));
        await page.CreateAsync(512);
        using var provider = Provider();
        Assert.AreEqual(512, (await provider.GetFileInfoAsync("disk.vhd")).Length);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Write(provider, "disk.vhd", "x"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Append(provider, "disk.vhd", "x"));
        Assert.AreEqual(BlobType.Page, (await page.GetPropertiesAsync()).Value.BlobType);
    }

    // ----- Reads -----

    [TestMethod]
    public async Task Range_EdgesKeepTheVersionCheck()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "0123456789");
        await Write(provider, "empty.txt", "");
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        const string stale = "\"0x8D000000000000\"";
        await using (var empty = await provider.ReadRangeAsync("empty.txt", 0, 5))
            Assert.AreEqual(-1, empty.ReadByte());
        await using (var past = await provider.ReadRangeAsync("f.txt", 50, 5, new ReadOptions { IfMatch = etag }))
            Assert.AreEqual(-1, past.ReadByte());
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 50, 5, new ReadOptions { IfMatch = stale }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("f.txt", 2, 0, new ReadOptions { IfMatch = stale }));
        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.ReadRangeAsync("missing.txt", 2, 0));
    }

    [TestMethod]
    public async Task Seekable_ADeletedFileThrowsIOException()
    {
        using var provider = Provider();
        await provider.UpdateFileAsync("f.bin", new MemoryStream(TestStreams.Bytes(1000)));
        await using var stream = await provider.OpenReadSeekableAsync("f.bin", new SeekableReadOptions { BufferSize = 64 });
        var buffer = new byte[10];
        await stream.ReadExactlyAsync(buffer);
        await provider.DeleteAsync("f.bin");
        stream.Position = 500;
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(buffer));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write(buffer, 0, 1));
        Assert.IsFalse(stream.CanWrite);
        stream.Flush();
    }

    [TestMethod]
    public async Task Seekable_SyncReadsWork()
    {
        using var provider = Provider();
        var bytes = TestStreams.Bytes(300);
        await provider.UpdateFileAsync("f.bin", new MemoryStream(bytes));
        using var stream = await provider.OpenReadSeekableAsync("f.bin", new SeekableReadOptions { BufferSize = 64 });
        stream.Seek(100, SeekOrigin.Begin);
        var buffer = new byte[50];
        stream.ReadExactly(buffer, 0, 50);
        CollectionAssert.AreEqual(bytes[100..150], buffer);
        await Write(provider, "f.bin", "changed");
        stream.Position = 250;
        Assert.ThrowsExactly<PreconditionFailedException>(() => stream.ReadExactly(buffer, 0, 10));
    }

    // ----- Listing -----

    [TestMethod]
    public async Task Listing_ShowsOnlyWhatTheProviderCanAddress()
    {
        // A client that keeps a trailing slash, to write a folder marker as the Azure portal does.
        var raw = new BlobServiceClient(Azurite.ServiceUri, new Azure.Storage.StorageSharedKeyCredential(Azurite.AccountName, Azurite.AccountKey), new BlobClientOptions { TrimBlobNameSlashes = false })
            .GetBlobContainerClient(_containerName);
        foreach (var name in new[] { "ok.txt", "dir//double.txt", ".wfp-tmp/abc", "marker/", "sub/.wfp-x", " ", "%2e%2e" })
            await raw.GetBlobClient(Name(name)).UploadAsync(BinaryData.FromString("x"));
        using var provider = Provider();
        var top = (await provider.GetDirectoryContentsAsync("")).Select(e => (e.Name, e.IsDirectory)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(new[] { ("dir", true), ("marker", true), ("ok.txt", false), ("sub", true) }, top, string.Join(", ", top));
        Assert.AreEqual(0, (await provider.GetDirectoryContentsAsync("dir")).Count());
        Assert.IsTrue((await provider.GetDirectoryContentsAsync("marker")).Exists, "A folder marker makes a folder");

        var all = new List<string>();
        await foreach (var entry in provider.EnumerateAsync("", recursive: true))
            all.Add(entry.Path.ToString() + (entry.IsDirectory ? "/" : ""));
        CollectionAssert.AreEquivalent(new[] { "dir/", "marker/", "ok.txt", "sub/" }, all, string.Join(", ", all));
    }

    [TestMethod]
    public async Task Listing_RecursiveIsOneFlatListing()
    {
        using var provider = Provider();
        foreach (var name in new[] { "a.txt", "d/b.txt", "d/e/c.txt", "d/e/f/g.txt", "h/i.txt" })
            await Write(provider, name, "x");
        var listings = _handler.Log.Count(l => l.Contains("comp=list", StringComparison.Ordinal));
        var all = new List<IWriteableFileInfo>();
        await foreach (var entry in provider.EnumerateAsync("", recursive: true))
            all.Add(entry);
        Assert.AreEqual(listings + 1, _handler.Log.Count(l => l.Contains("comp=list", StringComparison.Ordinal)));
        var paths = all.ConvertAll(e => e.Path.ToString());
        CollectionAssert.AreEquivalent(new[] { "a.txt", "d", "d/b.txt", "d/e", "d/e/c.txt", "d/e/f", "d/e/f/g.txt", "h", "h/i.txt" }, paths);
        Assert.IsTrue(all.Where(e => !e.IsDirectory).All(e => e.Exists && e.Length == 1 && e.ETag is not null));
    }

    [TestMethod]
    public async Task ETag_IsTheSameFromAListingAndFromFileInfo()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "x");
        var listed = (await provider.GetDirectoryContentsAsync("")).Cast<IWriteableFileInfo>().Single();
        var info = await provider.GetFileInfoAsync("f.txt");
        Assert.AreEqual(info.ETag, listed.ETag);
        Assert.AreEqual(info.LastModified, listed.LastModified);
        await Write(provider, "f.txt", "y", new UpdateFileOptions { IfMatch = listed.ETag });
        Assert.AreEqual("y", await ReadRaw("f.txt"));
    }

    [TestMethod]
    public async Task Delete_AFolderDeletesEveryBlobUnderItOnly()
    {
        using var provider = Provider();
        var names = Enumerable.Range(0, 120).Select(i => $"big/sub{i % 3}/{(i % 7 == 0 ? "deep/" : "")}f{i}.txt").ToList();
        await Parallel.ForEachAsync(names, async (name, ct) => await _raw.GetBlobClient(Name(name)).UploadAsync(BinaryData.FromString("x"), ct));
        await Write(provider, "big2/keep.txt", "keep");
        await Write(provider, "big", "a key named like the folder");
        Assert.IsTrue(await provider.DeleteAsync("big"));
        Assert.IsTrue(await provider.DeleteAsync("big"), "The key went first; the folder second");
        Assert.IsFalse(await provider.DeleteAsync("big"));
        Assert.AreEqual(0, await CountUnder("big/"));
        Assert.AreEqual("keep", await ReadRaw("big2/keep.txt"));
    }

    private async Task<int> CountUnder(string prefix)
    {
        var count = 0;
        await foreach (var _ in _raw.GetBlobsAsync(new GetBlobsOptions { Prefix = Name(prefix) }))
            count++;
        return count;
    }

    // ----- Moves and copies -----

    [TestMethod]
    public async Task Move_OfASourceChangedAfterTheCopyKeepsTheSourceAndRemovesTheCopy()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        _handler.Intercept = r =>
        {
            if (r.Method == HttpMethod.Delete && AzureHandler.BlobName(r) == Name("src.txt"))
                _raw.GetBlockBlobClient(Name("src.txt")).Upload(new MemoryStream(Encoding.UTF8.GetBytes("v2")));
            return null;
        };
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("src.txt", "dst.txt"));
        _handler.Intercept = null;
        StringAssert.Contains(e.Message, "the copy at 'dst.txt' was removed");
        Assert.AreEqual("v2", await ReadRaw("src.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("dst.txt")).Exists, "The copy was removed");
    }

    [TestMethod]
    public async Task Move_RollbackThatFailsSaysTheCopyIsStillThere()
    {
        using var provider = Provider(retries: 0);
        await Write(provider, "src.txt", "v1");
        _handler.Intercept = r =>
        {
            if (r.Method != HttpMethod.Delete)
                return null;
            if (AzureHandler.BlobName(r) == Name("src.txt"))
                _raw.GetBlockBlobClient(Name("src.txt")).Upload(new MemoryStream(Encoding.UTF8.GetBytes("v2")));
            return AzureHandler.BlobName(r) == Name("dst.txt") ? AzureHandler.Error(HttpStatusCode.InternalServerError, "InternalError") : null;
        };
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.MoveAsync("src.txt", "dst.txt"));
        _handler.Intercept = null;
        StringAssert.Contains(e.Message, "could not be removed and is still there");
        Assert.AreEqual("v1", await ReadRaw("dst.txt"));
    }

    [TestMethod]
    public async Task Move_OfASourceDeletedAfterTheCopyCompletes()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        _handler.Intercept = r =>
        {
            if (r.Method == HttpMethod.Delete && AzureHandler.BlobName(r) == Name("src.txt"))
                _raw.GetBlobClient(Name("src.txt")).DeleteIfExists();
            return null;
        };
        await provider.MoveAsync("src.txt", "dst.txt");
        Assert.AreEqual("v1", await ReadRaw("dst.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("src.txt")).Exists);
    }

    [TestMethod]
    public async Task Copy_ReadsTheSourceAgainWhenItChangesBeforeTheCopy()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        var raced = false;
        _handler.Intercept = r =>
        {
            if (raced || !AzureHandler.IsCopy(r))
                return null;
            raced = true;
            _raw.GetBlockBlobClient(Name("src.txt")).Upload(new MemoryStream(Encoding.UTF8.GetBytes("v2")));
            return null;
        };
        await provider.CopyAsync("src.txt", "dst.txt");
        Assert.AreEqual("v2", await ReadRaw("dst.txt"));
        Assert.AreEqual(2, _handler.Log.Count(l => l.EndsWith("copy", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Copy_ThatFailsOnTheServerLeavesNoDestination()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        var started = false;
        _handler.Intercept = r =>
        {
            if (AzureHandler.BlobName(r) != Name("dst.txt"))
                return null;
            if (AzureHandler.IsCopy(r))
            {
                started = true;
                return CopyResponse(HttpStatusCode.Accepted, "pending");
            }
            return started && r.Method == HttpMethod.Head ? CopyResponse(HttpStatusCode.OK, "failed") : null;
        };
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.CopyAsync("src.txt", "dst.txt"));
        StringAssert.Contains(e.Message, "Failed");
        Assert.IsTrue(_handler.For(Name("dst.txt")).Contains($"DELETE {Name("dst.txt")}"), "What the failed copy left is removed");
    }

    [TestMethod]
    public async Task Copy_CancelledWhileItRunsIsAborted()
    {
        using var provider = Provider();
        provider.CopyPollInterval = TimeSpan.FromMilliseconds(20);
        await Write(provider, "src.txt", "v1");
        var started = false;
        _handler.Intercept = r =>
        {
            if (AzureHandler.BlobName(r) != Name("dst.txt") || AzureHandler.Comp(r) == "copy")
                return null;
            if (AzureHandler.IsCopy(r))
            {
                started = true;
                return CopyResponse(HttpStatusCode.Accepted, "pending");
            }
            return started && r.Method == HttpMethod.Head ? CopyResponse(HttpStatusCode.OK, "pending") : null;
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.CopyAsync("src.txt", "dst.txt", cancel.Token));
        Assert.IsTrue(_handler.For(Name("dst.txt")).Contains($"PUT {Name("dst.txt")}?comp=copy"), "The copy was aborted");
    }

    private static HttpResponseMessage CopyResponse(HttpStatusCode status, string copyStatus)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0x8D00000000000C1\"");
        response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        response.Headers.Add("x-ms-copy-id", "11111111-1111-1111-1111-111111111111");
        response.Headers.Add("x-ms-copy-status", copyStatus);
        response.Headers.Add("x-ms-blob-type", "BlockBlob");
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        if (copyStatus == "failed")
            response.Headers.Add("x-ms-copy-status-description", "500 InternalError (test)");
        return response;
    }

    // ----- Missing container (P18, F44) -----

    [TestMethod]
    public async Task MissingContainer_ReadsNeverCreateIt()
    {
        var name = Azurite.NewContainerName();
        using var provider = new WriteableAzureBlobFileProvider(Azurite.Options(name));
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("a.txt"));
        StringAssert.Contains(e.Message, name);
        StringAssert.Contains(e.Message, nameof(AzureFileProviderOptions.CreateContainerIfMissing));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.OpenReadSeekableAsync("a.txt"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReadRangeAsync("a.txt", 0, 1));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.CopyAsync("a.txt", "b.txt"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Permission = SignedUrlAccess.Write }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => { await foreach (var _ in provider.EnumerateAsync("", recursive: true)) { } });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.UpdateFileAsync("a.txt", TestStreams.Text("x"), UpdateFileOptions.Append));
        Assert.IsFalse((await Azurite.Container(name).ExistsAsync()).Value);
    }

    [TestMethod]
    public async Task MissingContainer_IsCreatedByTheFirstWriteWhenAllowed()
    {
        foreach (var write in new Func<IWriteableFileProvider, Task>[]
        {
            p => Write(p, "a.txt", "x"),
            p => Write(p, "a.txt", ""),
            p => Append(p, "a.txt", "x"),
            p => p.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Permission = SignedUrlAccess.Create }),
        })
        {
            var name = Azurite.NewContainerName();
            try
            {
                var options = Azurite.Options(name);
                options.CreateContainerIfMissing = true;
                using var provider = new WriteableAzureBlobFileProvider(options);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetDirectoryContentsAsync(""), "Reads still refuse");
                await write(provider);
                Assert.IsTrue((await Azurite.Container(name).ExistsAsync()).Value);
            }
            finally
            {
                await Azurite.DeleteContainerAsync(name);
            }
        }
    }

    // ----- Signed URLs, used -----

    [TestMethod]
    public async Task SignedUrls_WorkOverHttp()
    {
        using var provider = new WriteableAzureBlobFileProvider(Azurite.Options(_containerName, _root));
        await Write(provider, "f.txt", "content");
        using var http = new HttpClient();

        var read = await provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), DownloadFileName = "résumé.txt", ContentType = "text/x-test" });
        using (var response = await http.GetAsync(read.Url))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("content", await response.Content.ReadAsStringAsync());
            Assert.AreEqual("text/x-test", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("résumé.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        }

        var create = await provider.GetSignedUrlAsync("new.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Create });
        Assert.AreEqual(HttpStatusCode.Created, (await Put(http, create, "created")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Put(http, create, "again")).StatusCode, "A create URL refuses an existing file");
        Assert.AreEqual("created", await Read(provider, "new.txt"));

        var write = await provider.GetSignedUrlAsync("new.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write });
        Assert.AreEqual(HttpStatusCode.Created, (await Put(http, write, "replaced")).StatusCode);
        Assert.AreEqual("replaced", await Read(provider, "new.txt"));

        var tampered = new Uri(read.Url.ToString().Replace("sp=r", "sp=w", StringComparison.Ordinal));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.GetAsync(tampered)).StatusCode);
    }

    private static async Task<HttpResponseMessage> Put(HttpClient http, SignedUrl url, string content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url.Url) { Content = new StringContent(content) };
        foreach (var (name, value) in url.Headers)
            request.Headers.Add(name, value);
        return await http.SendAsync(request);
    }

    /// <summary>A stream that can only be read forward.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
