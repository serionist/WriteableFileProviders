using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// Races and failures in the middle of an operation, injected through the HTTP pipeline: another writer acts just before
/// the provider's next request is sent.
/// </summary>
public sealed partial class AzureBlobProviderTests
{
    // Runs the action once, just before the first request that matches is sent.
    private void Before(Func<HttpRequestMessage, bool> matches, Action action)
    {
        var done = false;
        _handler.Intercept = r =>
        {
            if (!done && matches(r))
            {
                done = true;
                action();
            }
            return null;
        };
    }

    private void RawUpload(string path, string content) =>
        _raw.GetBlockBlobClient(Name(path)).Upload(new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static bool IsTempCommit(HttpRequestMessage r) => IsCommit(r) && AzureHandler.BlobName(r).Contains("/.wfp-tmp/", StringComparison.Ordinal);

    // ----- Overwriting an append blob -----

    [TestMethod]
    public async Task LegacyAppendBlob_EmptyOverwrite()
    {
        await LegacyAppendBlob("legacy.txt", "old");
        using var provider = Provider();
        await Write(provider, "legacy.txt", "");
        var properties = (await _raw.GetBlobClient(Name("legacy.txt")).GetPropertiesAsync()).Value;
        Assert.AreEqual(BlobType.Block, properties.BlobType);
        Assert.AreEqual(0, properties.ContentLength);
    }

    [TestMethod]
    public async Task LegacyAppendBlob_SourceThatFailsLeavesItUntouched()
    {
        var legacy = await LegacyAppendBlob("legacy.txt", "old");
        using var provider = Provider();
        provider.BlockSize = 2;
        await Assert.ThrowsAsync<IOException>(() => provider.UpdateFileAsync("legacy.txt", new FailingStream([1, 2, 3, 4, 5])));
        Assert.AreEqual("old", await ReadRaw("legacy.txt"));
        Assert.AreEqual(BlobType.Append, (await legacy.GetPropertiesAsync()).Value.BlobType);
        Assert.AreEqual(0, (await TempBlobs()).Count, "The temp blob is removed");
    }

    [TestMethod]
    public async Task LegacyAppendBlob_OverwriteWhoseCopyFailsKeepsTheNamedTempBlob()
    {
        await LegacyAppendBlob("legacy.txt", "old");
        using var provider = Provider(retries: 0);
        _handler.Intercept = r => AzureHandler.IsCopy(r) && AzureHandler.BlobName(r) == Name("legacy.txt") ? AzureHandler.Error(HttpStatusCode.InternalServerError, "InternalError") : null;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "legacy.txt", "brand new"));
        _handler.Intercept = null;
        var temp = (await TempBlobs()).Single()[(_root.Length + 1)..];
        StringAssert.Contains(e.Message, temp);
        Assert.AreEqual("brand new", await Read(provider, temp));
        Assert.IsFalse((await provider.GetFileInfoAsync("legacy.txt")).Exists);
    }

    [TestMethod]
    public async Task LegacyAppendBlob_ReplacedByABlockBlobMeanwhileIsOverwritten()
    {
        await LegacyAppendBlob("legacy.txt", "old");
        using var provider = Provider();
        // Through the handler, which tracks blob types: Azure lets no Put Blob replace an append blob, so the other writer
        // deletes it first.
        Before(IsTempCommit, () =>
        {
            var tracked = Azurite.Container(_containerName, _handler).GetBlobClient(Name("legacy.txt"));
            tracked.DeleteIfExists();
            tracked.Upload(BinaryData.FromString("someone else's"));
        });
        await Write(provider, "legacy.txt", "mine");
        Assert.AreEqual("mine", await ReadRaw("legacy.txt"));
        Assert.AreEqual(0, (await TempBlobs()).Count);
    }

    [TestMethod]
    public async Task LegacyAppendBlob_DeletedMeanwhileIsCreated()
    {
        await LegacyAppendBlob("legacy.txt", "old");
        using var provider = Provider();
        Before(IsTempCommit, () => Azurite.Container(_containerName, _handler).GetBlobClient(Name("legacy.txt")).DeleteIfExists());
        await Write(provider, "legacy.txt", "mine");
        Assert.AreEqual("mine", await ReadRaw("legacy.txt"));
    }

    [TestMethod]
    public async Task LegacyAppendBlob_DeletedMeanwhileFailsAnIfMatch()
    {
        var legacy = await LegacyAppendBlob("legacy.txt", "old");
        var etag = (await legacy.GetPropertiesAsync()).Value.ETag.ToString("H");
        using var provider = Provider();
        Before(IsTempCommit, () => Azurite.Container(_containerName, _handler).GetBlobClient(Name("legacy.txt")).DeleteIfExists());
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "legacy.txt", "mine", new UpdateFileOptions { IfMatch = etag }));
        Assert.IsFalse((await provider.GetFileInfoAsync("legacy.txt")).Exists);
        Assert.AreEqual(0, (await TempBlobs()).Count, "Nothing of the caller's was destroyed, so the temp blob goes");
    }

    [TestMethod]
    public async Task LegacyAppendBlob_EmptyIsConverted()
    {
        await LegacyAppendBlob("legacy.txt", "");
        using var provider = Provider();
        await Append(provider, "legacy.txt", "first");
        Assert.AreEqual("first", await ReadRaw("legacy.txt"));
    }

    // ----- Appending -----

    [TestMethod]
    public async Task Append_CreatingRacesWithAnotherCreator()
    {
        using var provider = Provider();
        Before(r => IsCommit(r) && AzureHandler.BlobName(r) == Name("f.txt"), () => RawUpload("f.txt", "theirs;"));
        await Append(provider, "f.txt", "mine;");
        Assert.AreEqual("theirs;mine;", await ReadRaw("f.txt"));
    }

    [TestMethod]
    public async Task Append_FileDeletedBetweenItsReads()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "gone;");
        Before(r => r.Method == HttpMethod.Get && AzureHandler.Comp(r) == "blocklist", () => _raw.GetBlobClient(Name("f.txt")).DeleteIfExists());
        await Append(provider, "f.txt", "mine;");
        Assert.AreEqual("mine;", await ReadRaw("f.txt"));
    }

    [TestMethod]
    public async Task Append_FileChangedBetweenItsReads()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "start;");
        Before(r => r.Method == HttpMethod.Get && AzureHandler.Comp(r) == "blocklist", () => RawUpload("f.txt", "theirs;"));
        await Append(provider, "f.txt", "mine;");
        Assert.AreEqual("theirs;mine;", await ReadRaw("f.txt"));
    }

    [TestMethod]
    public async Task Append_SourceChangedWhileRestagedOnTheServer()
    {
        await _raw.GetBlobClient(Name("put.txt")).UploadAsync(BinaryData.FromString("v1;"));
        using var provider = Provider();
        Before(r => AzureHandler.Comp(r) == "block" && AzureHandler.IsCopy(r), () => _raw.GetBlobClient(Name("put.txt")).Upload(BinaryData.FromString("v2;"), overwrite: true));
        await Append(provider, "put.txt", "mine;");
        Assert.AreEqual("v2;mine;", await ReadRaw("put.txt"));
    }

    [TestMethod]
    public async Task Append_SourceChangedWhileRestagedThroughTheClient()
    {
        await _raw.GetBlobClient(Name("put.txt")).UploadAsync(BinaryData.FromString("v1;"));
        var sas = _raw.GenerateSasUri(BlobContainerSasPermissions.All, DateTimeOffset.UtcNow.AddHours(1));
        using var provider = new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { RootDirectoryPath = _root }, new BlobContainerClient(sas, Azurite.ClientOptions(_handler)));
        Before(r => r.Method == HttpMethod.Get && AzureHandler.Comp(r) is null && AzureHandler.BlobName(r) == Name("put.txt"),
            () => _raw.GetBlobClient(Name("put.txt")).Upload(BinaryData.FromString("v2;"), overwrite: true));
        await Append(provider, "put.txt", "mine;");
        Assert.AreEqual("v2;mine;", await ReadRaw("put.txt"));
    }

    [TestMethod]
    public async Task Append_WithNoRoomForTheExistingContentChangesNothing()
    {
        await _raw.GetBlobClient(Name("put.txt")).UploadAsync(BinaryData.FromString("base;"));
        _handler = new AzureHandler { MaxBlocks = 1 };
        using var provider = Provider();
        provider.MaxCommittedBlocks = 1;
        var e = await Assert.ThrowsAsync<IOException>(() => Append(provider, "put.txt", "more"));
        StringAssert.Contains(e.Message, "The file and the content appended to it need more than 1 blocks");
        Assert.AreEqual("base;", await ReadRaw("put.txt"));

        // The content appended sizes its blocks to fit: more than one block's worth still makes one block.
        var bytes = TestStreams.Bytes(provider.BlockSize + 1);
        await provider.UpdateFileAsync("new.txt", new MemoryStream(bytes), UpdateFileOptions.Append);
        Assert.AreEqual(1, (await CommittedBlocks("new.txt")).Count);
        CollectionAssert.AreEqual(bytes, (await _raw.GetBlobClient(Name("new.txt")).DownloadContentAsync()).Value.Content.ToArray());
    }

    [TestMethod]
    public async Task Append_KeepsATierSetOnTheBlob()
    {
        using var provider = Provider();
        await Write(provider, "cool.txt", "a");
        await _raw.GetBlobClient(Name("cool.txt")).SetAccessTierAsync(AccessTier.Cool);
        await Append(provider, "cool.txt", "b");
        var properties = (await _raw.GetBlobClient(Name("cool.txt")).GetPropertiesAsync()).Value;
        Assert.AreEqual("ab", await ReadRaw("cool.txt"));
        Assert.AreEqual(AccessTier.Cool.ToString(), properties.AccessTier);
    }

    // ----- Overwriting -----

    [TestMethod]
    public async Task Overwrite_IfMatchOfAMissingFileFails()
    {
        using var provider = Provider();
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "missing.txt", "x", new UpdateFileOptions { IfMatch = "\"0x8D000000000000\"" }));
        Assert.IsFalse((await provider.GetFileInfoAsync("missing.txt")).Exists);
    }

    [TestMethod]
    public async Task Overwrite_FromASeekableStreamWithoutALength()
    {
        using var provider = Provider();
        await provider.UpdateFileAsync("f.txt", new NoLengthStream(Encoding.UTF8.GetBytes("no length")));
        Assert.AreEqual("no length", await ReadRaw("f.txt"));
    }

    // ----- Copies -----

    [TestMethod]
    public async Task Copy_OfASourceDeletedJustBeforeThrowsFileNotFound()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        Before(AzureHandler.IsCopy, () => _raw.GetBlobClient(Name("src.txt")).DeleteIfExists());
        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.CopyAsync("src.txt", "dst.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("dst.txt")).Exists);
    }

    [TestMethod]
    public async Task Copy_OntoADestinationCreatedJustBeforeThrowsFileExists()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        Before(AzureHandler.IsCopy, () => RawUpload("dst.txt", "theirs"));
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("src.txt", "dst.txt"));
        Assert.AreEqual("theirs", await ReadRaw("dst.txt"));
    }

    [TestMethod]
    public async Task Copy_OfASourceThatKeepsChangingGivesUp()
    {
        using var provider = Provider();
        provider.MaxAttempts = 2;
        await Write(provider, "src.txt", "v0");
        var version = 0;
        _handler.Intercept = r =>
        {
            if (AzureHandler.IsCopy(r))
                RawUpload("src.txt", $"v{++version}");
            return null;
        };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.CopyAsync("src.txt", "dst.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("dst.txt")).Exists);
    }

    // ----- What a move deletes -----

    [TestMethod]
    public async Task DeleteFile_RemovesTheKeyAndNotTheFolder()
    {
        using var provider = Provider();
        await Write(provider, "k", "key");
        await Write(provider, "k/x.txt", "below");
        await provider.DeleteFileForTestsAsync("k");
        Assert.IsTrue((await provider.GetFileInfoAsync("k")).IsDirectory, "The key is gone; the folder of the same name stays");
        Assert.AreEqual("below", await ReadRaw("k/x.txt"));
    }

    [TestMethod]
    public async Task SignedUrl_OfAnUnknownAccessIsRefused()
    {
        using var provider = Provider();
        Assert.AreSame(TimeProvider.System, provider.Clock);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Permission = (SignedUrlAccess)99 }));
    }

    /// <summary>A stream that says it can seek but cannot tell its length.</summary>
    private sealed class NoLengthStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override long Length => throw new NotSupportedException();
    }
}
