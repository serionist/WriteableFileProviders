using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// Writers that meet another writer's commit, copies that fail or hang, snapshots, and leases: what the provider does
/// when Azure says no in the middle of an operation.
/// </summary>
public sealed partial class AzureBlobProviderTests
{
    // Another writer stages one block and commits it: Azure drops every block the commit does not list, the provider's too.
    private void CompetingCommit(string path, string content)
    {
        var blob = _raw.GetBlockBlobClient(Name(path));
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        blob.StageBlock(id, new MemoryStream(Encoding.UTF8.GetBytes(content)));
        blob.CommitBlockList([id]);
    }

    private static HttpResponseMessage Accepted(HttpStatusCode status = HttpStatusCode.NoContent)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        return response;
    }

    // ----- Overwrites that meet another writer's commit (F1) -----

    [TestMethod]
    public async Task Overwrite_OfOneBlockStagesAgainWhenAnotherCommitDroppedIt()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "old");
        Before(r => IsCommit(r) && AzureHandler.BlobName(r) == Name("f.txt"), () => CompetingCommit("f.txt", "theirs"));
        await Write(provider, "f.txt", "mine");
        Assert.AreEqual("mine", await ReadRaw("f.txt"), "The last writer wins, as an overwrite promises");
        Assert.AreEqual(3, _handler.For(Name("f.txt")).Count(l => l.StartsWith("PUT", StringComparison.Ordinal) && l.Contains("comp=blocklist", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Overwrite_OfManyBlocksStoresNothingWhenAnotherCommitDroppedThem()
    {
        using var provider = Provider();
        provider.BlockSize = 2;
        await Write(provider, "f.txt", "old");
        Before(r => IsCommit(r) && AzureHandler.BlobName(r) == Name("f.txt"), () => CompetingCommit("f.txt", "theirs"));
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "f.txt", "mine, longer"));
        StringAssert.Contains(e.Message, "nothing of this write was stored");
        Assert.AreEqual("theirs", await ReadRaw("f.txt"));
    }

    [TestMethod]
    public async Task Overwrite_WithAConditionFailsItWhenAnotherCommitWentFirst()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "old");
        var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
        Before(r => IsCommit(r) && AzureHandler.BlobName(r) == Name("f.txt"), () => CompetingCommit("f.txt", "theirs"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write(provider, "f.txt", "mine", new UpdateFileOptions { IfMatch = etag }));
        Assert.AreEqual("theirs", await ReadRaw("f.txt"));
    }

    // ----- Copies that fail, hang or are cancelled (F3, F6) -----

    // An append blob replaced by a block blob while the provider overwrites it: the provider copies its temp blob over
    // the block blob, and that copy is what fails, hangs or is cancelled.
    private async Task<WriteableAzureBlobFileProvider> OverwriteRacingABlockBlob(Func<bool, HttpResponseMessage> head)
    {
        await LegacyAppendBlob("legacy.txt", "old");
        var provider = Provider();
        var started = false;
        var replaced = false;
        _handler.Intercept = r =>
        {
            if (!replaced && IsTempCommit(r))
            {
                replaced = true;
                var tracked = Azurite.Container(_containerName, _handler).GetBlobClient(Name("legacy.txt"));
                tracked.DeleteIfExists();
                tracked.Upload(BinaryData.FromString("someone else's"));
                return null;
            }
            if (AzureHandler.BlobName(r) != Name("legacy.txt"))
                return null;
            if (AzureHandler.IsCopy(r))
            {
                started = true;
                return CopyResponse(HttpStatusCode.Accepted, "pending");
            }
            if (AzureHandler.Comp(r) == "copy")
                return Accepted();
            return started && r.Method == HttpMethod.Head ? head(started) : null;
        };
        return provider;
    }

    [TestMethod]
    public async Task CopyOverABlockBlobThatFails_KeepsTheNamedTempBlobAndDeletesNothing()
    {
        using var provider = await OverwriteRacingABlockBlob(_ => CopyResponse(HttpStatusCode.OK, "failed"));
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => Write(provider, "legacy.txt", "mine"));
        _handler.Intercept = null;
        var temp = (await TempBlobs()).Single()[(_root.Length + 1)..];
        StringAssert.Contains(e.Message, temp);
        Assert.AreEqual("mine", await Read(provider, temp));
        Assert.AreEqual(1, _handler.For(Name("legacy.txt")).Count(l => l.StartsWith("DELETE", StringComparison.Ordinal)), "Only the other writer's delete");
    }

    [TestMethod]
    public async Task CopyOverABlockBlobThatIsCancelled_IsAbortedAndNeverDeleted()
    {
        using var provider = await OverwriteRacingABlockBlob(_ => CopyResponse(HttpStatusCode.OK, "pending"));
        provider.CopyPollInterval = TimeSpan.FromMilliseconds(20);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var e = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.UpdateFileAsync("legacy.txt", Testing.TestStreams.Text("mine"), cancellationToken: cancel.Token));
        _handler.Intercept = null;
        var temp = (await TempBlobs()).Single()[(_root.Length + 1)..];
        StringAssert.Contains(e.Message, temp);
        var requests = _handler.For(Name("legacy.txt"));
        Assert.IsTrue(requests.Contains($"PUT {Name("legacy.txt")}?comp=copy"), "The copy was aborted");
        Assert.AreEqual(1, requests.Count(l => l.StartsWith("DELETE", StringComparison.Ordinal)), "The file it was overwriting is not deleted");
    }

    [TestMethod]
    public async Task Copy_ThatStaysPendingIsAbortedAfterTheTimeout()
    {
        using var provider = Provider();
        provider.CopyPollInterval = TimeSpan.FromMilliseconds(20);
        provider.CopyTimeout = TimeSpan.FromMilliseconds(300);
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
            if (AzureHandler.Comp(r) == "copy")
                return Accepted();
            return started && r.Method == HttpMethod.Head ? CopyResponse(HttpStatusCode.OK, "pending") : null;
        };
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.CopyAsync("src.txt", "dst.txt"));
        StringAssert.Contains(e.Message, "did not complete within");
        var requests = _handler.For(Name("dst.txt"));
        Assert.IsTrue(requests.Contains($"PUT {Name("dst.txt")}?comp=copy"), "The copy was aborted");
        Assert.IsTrue(requests.Contains($"DELETE {Name("dst.txt")}"), "What the copy was making is removed");
    }

    // ----- Snapshots are never deleted (F4), except by the append blob conversion (P30) -----

    [TestMethod]
    public async Task Delete_RefusesABlobWithSnapshots()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "keep");
        await _raw.GetBlobClient(Name("f.txt")).CreateSnapshotAsync();
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("f.txt"));
        StringAssert.Contains(e.Message, "snapshots");
        StringAssert.Contains(e.Message, "'f.txt'");
        Assert.AreEqual("keep", await ReadRaw("f.txt"));

        await Write(provider, "d/a.txt", "a");
        await Write(provider, "d/b.txt", "b");
        await _raw.GetBlobClient(Name("d/b.txt")).CreateSnapshotAsync();
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("d"));
        Assert.AreEqual("b", await ReadRaw("d/b.txt"));
    }

    [TestMethod]
    public async Task Move_RefusesASourceWithSnapshots()
    {
        using var provider = Provider();
        await Write(provider, "src.txt", "v1");
        await _raw.GetBlobClient(Name("src.txt")).CreateSnapshotAsync();
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.MoveAsync("src.txt", "dst.txt"));
        StringAssert.Contains(e.Message, "snapshots");
        StringAssert.Contains(e.Message, "was removed");
        Assert.AreEqual("v1", await ReadRaw("src.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("dst.txt")).Exists);
    }

    [TestMethod]
    public async Task LegacyAppendBlob_ConversionDeletesItsSnapshots()
    {
        var legacy = await LegacyAppendBlob("legacy.txt", "old;");
        await legacy.CreateSnapshotAsync();
        using var provider = Provider();
        await Append(provider, "legacy.txt", "new;");
        Assert.AreEqual("old;new;", await ReadRaw("legacy.txt"));
        var snapshots = 0;
        await foreach (var item in _raw.GetBlobsAsync(new GetBlobsOptions { Prefix = Name("legacy.txt"), States = BlobStates.Snapshots }))
            snapshots += item.Snapshot is null ? 0 : 1;
        Assert.AreEqual(0, snapshots);
    }

    // ----- Appends that meet another writer (F9) -----

    [TestMethod]
    public async Task Append_ToAFileCreatedAsAnAppendBlobMeanwhileConvertsIt()
    {
        using var provider = Provider();
        Before(r => r.Method == HttpMethod.Put && AzureHandler.Comp(r) == "block" && AzureHandler.BlobName(r) == Name("f.txt"),
            () => Azurite.Container(_containerName, _handler).GetAppendBlobClient(Name("f.txt")).Create());
        await Append(provider, "f.txt", "mine;");
        Assert.AreEqual("mine;", await ReadRaw("f.txt"));
        Assert.AreEqual(BlobType.Block, (await _raw.GetBlobClient(Name("f.txt")).GetPropertiesAsync()).Value.BlobType);
    }

    // ----- A lease is not a condition (F10) -----

    [TestMethod]
    public async Task LeasedBlob_IsReportedAsItIsAndNotRetried()
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "leased");
        var lease = _raw.GetBlobClient(Name("f.txt")).GetBlobLeaseClient();
        await lease.AcquireAsync(TimeSpan.FromSeconds(60));
        try
        {
            var overwrite = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => Write(provider, "f.txt", "x"));
            Assert.AreEqual(412, overwrite.Status);
            var append = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => Append(provider, "f.txt", "x"));
            Assert.AreEqual(412, append.Status);
            Assert.AreEqual(1, _handler.For(Name("f.txt")).Count(l => l.StartsWith("HEAD", StringComparison.Ordinal)), "The append read the file once: no retries");
            Assert.AreEqual("leased", await ReadRaw("f.txt"));
        }
        finally
        {
            await lease.ReleaseAsync();
        }
    }
}
