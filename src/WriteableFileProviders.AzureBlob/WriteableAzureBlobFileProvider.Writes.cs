using System.Buffers;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace WriteableFileProviders.AzureBlob;

public partial class WriteableAzureBlobFileProvider
{
    // Random ids of 48 bytes, 64 characters in base64: unique across concurrent writers, and one length for every block
    // of a blob, as Azure requires.
    private const int BlockIdBytes = 48;
    private const int BlockIdLength = 64;

    /// <summary>
    /// Writes a file. An overwrite stages the content as blocks while it reads the stream and commits them in one request;
    /// an append spools the content, then commits the file's block list with the new blocks after it, on the condition
    /// that the file has not changed (D19). The conditions in <paramref name="options"/> are checked by the commit,
    /// atomically.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        await GuardAsync(() => options.Mode == UpdateFileMode.Append
            ? AppendAsync(path, contents, options, cancellationToken)
            : OverwriteAsync(path, contents, options, cancellationToken)).ConfigureAwait(false);
    }

    private async Task OverwriteAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        var blob = BlockBlob(path);
        var blockSize = BlockSizeFor(contents);
        var ids = new List<string>();
        bool more;
        using (var first = await Chunk.ReadAsync(contents, blockSize, cancellationToken).ConfigureAwait(false))
        {
            more = first.IsFull;
            if (first.Count > 0)
            {
                var id = NewBlockId();
                try
                {
                    await WithContainerAsync(() => StageAsync(blob, id, first, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                catch (RequestFailedException e) when (Is(e, BlobErrorCode.InvalidBlobType))
                {
                    // An append blob (or a page blob): Azure cannot stage blocks on it.
                    await ReplaceThroughTempAsync(path, first, more ? contents : null, blockSize, options, cancellationToken).ConfigureAwait(false);
                    return;
                }
                ids.Add(id);
            }
        }
        if (more)
            await StageRestAsync(blob, contents, blockSize, ids, cancellationToken).ConfigureAwait(false);

        try
        {
            await WithContainerAsync(() => blob.CommitBlockListAsync(ids, new CommitBlockListOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = options.ContentType ?? DefaultContentType },
                Conditions = WriteConditions(options),
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (ids.Count == 0 && Is(e, BlobErrorCode.InvalidBlobType))
        {
            using var empty = Chunk.Empty();
            await ReplaceThroughTempAsync(path, empty, null, blockSize, options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (IsConditionFailure(e, options))
        {
            throw PreconditionFailed(path, options, e);
        }
    }

    // Overwrites a blob that is not a block blob: the content goes to a temp blob first, which then takes its name.
    private async Task ReplaceThroughTempAsync(ResolvedPath path, Chunk first, Stream? rest, int blockSize, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        // Refuse before uploading anything when a condition already fails.
        CheckReplace(path, await GetPropertiesOrNullAsync(Blob(path), cancellationToken).ConfigureAwait(false), options);
        var temp = TempBlob();
        try
        {
            var ids = new List<string>();
            if (first.Count > 0)
            {
                var id = NewBlockId();
                await StageAsync(temp, id, first, cancellationToken).ConfigureAwait(false);
                ids.Add(id);
            }
            if (rest is not null)
                await StageRestAsync(temp, rest, blockSize, ids, cancellationToken).ConfigureAwait(false);
            await temp.CommitBlockListAsync(ids, new CommitBlockListOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = options.ContentType ?? DefaultContentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteQuietlyAsync(temp).ConfigureAwait(false);
            throw;
        }
        await MoveTempIntoAsync(path, temp, options, cancellationToken).ConfigureAwait(false);
    }

    // Gives the temp blob's content the file's name, whatever type of blob holds it now: a block blob is replaced by a
    // copy; an append blob is deleted first, if unchanged, then the copy lands on the free name. The temp blob is
    // deleted when it is no longer needed, and kept, named in the exception, when the file was deleted but the copy
    // did not land.
    private async Task MoveTempIntoAsync(ResolvedPath path, BlockBlobClient temp, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        var target = Blob(path);
        var deleted = false;
        var done = false;
        try
        {
            for (var attempt = 0; attempt < MaxAttempts && !done; attempt++)
            {
                var current = await GetPropertiesOrNullAsync(target, cancellationToken).ConfigureAwait(false);
                CheckReplace(path, current, options);
                if (current is null)
                {
                    done = await TryCopyAsync(temp, target, new BlobRequestConditions { IfNoneMatch = ETag.All }, null, cancellationToken).ConfigureAwait(false) is not null;
                }
                else if (current.BlobType == BlobType.Block)
                {
                    done = await TryCopyAsync(temp, target, new BlobRequestConditions { IfMatch = current.ETag }, null, cancellationToken).ConfigureAwait(false) is not null;
                }
                else if (await TryDeleteAsync(target, current.ETag, cancellationToken).ConfigureAwait(false))
                {
                    deleted = true;
                    done = await TryCopyAsync(temp, target, new BlobRequestConditions { IfNoneMatch = ETag.All }, null, cancellationToken).ConfigureAwait(false) is not null;
                }
            }
            if (!done)
                throw new PreconditionFailedException($"The file '{path}' kept changing while it was replaced; it was not written after {MaxAttempts} attempts.");
        }
        catch (Exception e) when (deleted && !done)
        {
            var kept = $"The file '{path}' was deleted to replace its append blob, but the new content did not reach it; it is kept at '{TempPath(temp)}'.";
            throw e is PreconditionFailedException ? new PreconditionFailedException(kept, e) : new IOException(kept, e);
        }
        finally
        {
            if (done || !deleted)
                await TryDeleteQuietlyAsync(temp).ConfigureAwait(false);
        }
    }

    private static void CheckReplace(ResolvedPath path, BlobProperties? current, UpdateFileOptions options)
    {
        if (current?.BlobType == BlobType.Page)
            throw new NotSupportedException($"The file '{path}' is a page blob, which {nameof(WriteableAzureBlobFileProvider)} reads but does not write.");
        if (options.IfNotExists && current is not null)
            throw new PreconditionFailedException($"The file '{path}' already exists.");
        if (options.IfMatch is { } ifMatch && (current is null || !SameTag(current.ETag, ifMatch)))
            throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{ifMatch}'.");
    }

    private async Task AppendAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        // The content is read once, before anything is sent: a retry after a concurrent change stages it again.
        var spool = await SpoolToTempFileAsync(contents, cancellationToken).ConfigureAwait(false);
        await using (spool.ConfigureAwait(false))
        {
            var blob = BlockBlob(path);
            var newBlocks = (int)Math.Min(int.MaxValue, (spool.Length + BlockSize - 1) / BlockSize);
            if (newBlocks > MaxCommittedBlocks)
                throw TooManyBlocks(BlockSize);
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var current = await WithContainerAsync(() => GetPropertiesOrNullAsync(blob, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    if (options.IfMatch is not null)
                        throw PreconditionFailed(path, options, null);
                    if (await TryCreateAsync(blob, spool, options, cancellationToken).ConfigureAwait(false))
                        return;
                    if (options.IfNotExists)
                        throw PreconditionFailed(path, options, null);
                    continue;
                }
                CheckReplace(path, current, options);
                var appended = current.BlobType == BlobType.Append
                    ? await ConvertAndAppendAsync(path, current, spool, newBlocks, options, cancellationToken).ConfigureAwait(false)
                    : await TryAppendBlocksAsync(blob, current, spool, newBlocks, options, cancellationToken).ConfigureAwait(false);
                if (appended)
                    return;
                // The file changed since it was read: a caller's condition no longer holds; otherwise read it again.
                if (options.IfMatch is not null)
                    throw PreconditionFailed(path, options, null);
            }
            throw new PreconditionFailedException($"The file '{path}' kept changing while it was appended to; nothing was appended after {MaxAttempts} attempts.");
        }
    }

    // Appending to a missing file creates it, unless someone else creates it first.
    private async Task<bool> TryCreateAsync(BlockBlobClient blob, FileStream spool, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await StageSpoolAsync(blob, spool, ids, cancellationToken).ConfigureAwait(false);
        try
        {
            await blob.CommitBlockListAsync(ids, new CommitBlockListOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = options.ContentType ?? DefaultContentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 412 || Is(e, BlobErrorCode.BlobAlreadyExists) || Is(e, BlobErrorCode.InvalidBlobType))
        {
            return false;
        }
    }

    // Commits the block list read with the new blocks after it, if the blob is still the version read. A blob whose
    // blocks cannot be reused as they are (written in one Put Blob, block ids of another length, or too many blocks for
    // the new ones) has its content staged again as new blocks first.
    private async Task<bool> TryAppendBlocksAsync(BlockBlobClient blob, BlobProperties current, FileStream spool, int newBlocks, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        BlockList list;
        try
        {
            list = (await blob.GetBlockListAsync(BlockListTypes.Committed, cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.BlobNotFound) || Is(e, BlobErrorCode.InvalidBlobType))
        {
            return false;
        }
        // The block list cannot be read on a condition; its ETag says whether it is the version read. Azure leaves it out
        // for a blob with no committed blocks; the conditional commit still guards that case.
        if (list.ETag != default && !SameTag(list.ETag, Tag(current.ETag)))
            return false;
        var committed = list.CommittedBlocks.ToList();
        var ids = committed.ConvertAll(b => b.Name);
        var reusable = committed.Sum(b => b.SizeLong) == current.ContentLength && ids.TrueForAll(id => id.Length == BlockIdLength);
        if (!reusable || ids.Count + newBlocks > MaxCommittedBlocks)
        {
            if (await RestageAsync(blob, blob, current, MaxCommittedBlocks - newBlocks, cancellationToken).ConfigureAwait(false) is not { } restaged)
                return false;
            ids = restaged;
        }
        await StageSpoolAsync(blob, spool, ids, cancellationToken).ConfigureAwait(false);
        try
        {
            await blob.CommitBlockListAsync(ids, new CommitBlockListOptions
            {
                HttpHeaders = CarriedHeaders(current, options.ContentType),
                Metadata = current.Metadata,
                AccessTier = CarriedTier(current),
                Conditions = new BlobRequestConditions { IfMatch = current.ETag },
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 412 || Is(e, BlobErrorCode.BlobNotFound))
        {
            return false;
        }
    }

    // P30: an append blob cannot become a block blob in place. Its content and the new bytes are committed to a temp
    // blob, the append blob is deleted if it is still the version read, and the temp blob is copied to its name.
    private async Task<bool> ConvertAndAppendAsync(ResolvedPath path, BlobProperties current, FileStream spool, int newBlocks, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        var target = Blob(path);
        var temp = TempBlob();
        var deleted = false;
        var done = false;
        try
        {
            if (await RestageAsync(temp, target, current, MaxCommittedBlocks - newBlocks, cancellationToken).ConfigureAwait(false) is not { } ids)
                return false;
            await StageSpoolAsync(temp, spool, ids, cancellationToken).ConfigureAwait(false);
            await temp.CommitBlockListAsync(ids, new CommitBlockListOptions
            {
                HttpHeaders = CarriedHeaders(current, options.ContentType),
                Metadata = current.Metadata,
                AccessTier = CarriedTier(current),
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            }, cancellationToken).ConfigureAwait(false);
            if (!await TryDeleteAsync(target, current.ETag, cancellationToken).ConfigureAwait(false))
                return false;
            deleted = true;
            if (await TryCopyAsync(temp, target, new BlobRequestConditions { IfNoneMatch = ETag.All }, null, cancellationToken).ConfigureAwait(false) is null)
                throw new PreconditionFailedException($"The file '{path}' was written by someone else while its append blob was converted.");
            done = true;
            return true;
        }
        catch (Exception e) when (deleted && !done)
        {
            var kept = $"The file '{path}' was deleted to convert its append blob, but the appended content did not reach it; the old and the new content are kept at '{TempPath(temp)}'.";
            throw e is PreconditionFailedException ? new PreconditionFailedException(kept, e) : new IOException(kept, e);
        }
        finally
        {
            if (done || !deleted)
                await TryDeleteQuietlyAsync(temp).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stages the content of <paramref name="source"/> as new blocks of <paramref name="target"/>, in ranges, on the
    /// condition that the source is still the version read: on the server (Put Block From URL) when the provider can sign
    /// a read SAS for it, else by downloading each range. Returns null when the source changed.
    /// </summary>
    private async Task<List<string>?> RestageAsync(BlockBlobClient target, BlobBaseClient source, BlobProperties current, int maxBlocks, CancellationToken cancellationToken)
    {
        var length = current.ContentLength;
        if (length == 0)
            return [];
        if (maxBlocks <= 0)
            throw TooManyBlocks(BlockSize);
        if (!_serverCopyRefused)
        {
            try
            {
                if (await RestageOnServerAsync(target, source, current, maxBlocks, cancellationToken).ConfigureAwait(false) is { } ids)
                    return ids.Count == 0 ? null : ids;
            }
            catch (RequestFailedException e) when (e.Status is 401 or 403)
            {
                // The service cannot read the source with the signature it was given: download the ranges instead.
                _serverCopyRefused = true;
            }
        }
        return await RestageThroughClientAsync(target, source, current, maxBlocks, cancellationToken).ConfigureAwait(false);
    }

    // Null when no source signature can be made; an empty list when the source changed.
    private async Task<List<string>?> RestageOnServerAsync(BlockBlobClient target, BlobBaseClient source, BlobProperties current, int maxBlocks, CancellationToken cancellationToken)
    {
        var length = current.ContentLength;
        var range = Math.Max(CopyRangeSize, (length + maxBlocks - 1) / maxBlocks);
        var ids = new List<string>();
        for (var offset = 0L; offset < length; offset += range)
        {
            if (await ReadSasUriAsync(source, cancellationToken).ConfigureAwait(false) is not { } sourceUri)
                return null;
            var id = NewBlockId();
            try
            {
                await target.StageBlockFromUriAsync(sourceUri, id, new StageBlockFromUriOptions
                {
                    SourceRange = new HttpRange(offset, Math.Min(range, length - offset)),
                    SourceConditions = new RequestConditions { IfMatch = current.ETag },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (e.Status == 412 || Is(e, BlobErrorCode.BlobNotFound))
            {
                return [];
            }
            ids.Add(id);
        }
        return ids;
    }

    private async Task<List<string>?> RestageThroughClientAsync(BlockBlobClient target, BlobBaseClient source, BlobProperties current, int maxBlocks, CancellationToken cancellationToken)
    {
        var length = current.ContentLength;
        var range = (int)Math.Min(MaxBlockSize, Math.Max(BlockSize, (length + maxBlocks - 1) / maxBlocks));
        if ((long)range * maxBlocks < length)
            throw TooManyBlocks(range);
        var ids = new List<string>();
        for (var offset = 0L; offset < length; offset += range)
        {
            var size = (int)Math.Min(range, length - offset);
            Chunk chunk;
            try
            {
                var download = await source.DownloadStreamingAsync(new BlobDownloadOptions
                {
                    Range = new HttpRange(offset, size),
                    Conditions = new BlobRequestConditions { IfMatch = current.ETag },
                }, cancellationToken).ConfigureAwait(false);
                using var content = download.Value.Content;
                chunk = await Chunk.ReadAsync(content, size, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (e.Status is 412 or 416 || Is(e, BlobErrorCode.BlobNotFound))
            {
                return null;
            }
            using (chunk)
            {
                if (chunk.Count != size)
                    return null;
                var id = NewBlockId();
                await StageAsync(target, id, chunk, cancellationToken).ConfigureAwait(false);
                ids.Add(id);
            }
        }
        return ids;
    }

    private Task StageSpoolAsync(BlockBlobClient blob, FileStream spool, List<string> ids, CancellationToken cancellationToken)
    {
        spool.Position = 0;
        return StageRestAsync(blob, spool, BlockSize, ids, cancellationToken);
    }

    // Reads the source to its end in blocks and stages them, a few at a time; adds their ids, in order, to ids.
    private async Task StageRestAsync(BlockBlobClient blob, Stream source, int blockSize, List<string> ids, CancellationToken cancellationToken)
    {
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task>();
        try
        {
            while (true)
            {
                if (pending.Count >= MaxConcurrency)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(finished);
                    await finished.ConfigureAwait(false);
                }
                var chunk = await Chunk.ReadAsync(source, blockSize, failed.Token).ConfigureAwait(false);
                if (chunk.Count == 0)
                {
                    chunk.Dispose();
                    break;
                }
                if (ids.Count >= MaxCommittedBlocks)
                {
                    chunk.Dispose();
                    throw TooManyBlocks(blockSize);
                }
                var id = NewBlockId();
                ids.Add(id);
                var full = chunk.IsFull;
                pending.Add(StageAndReleaseAsync(blob, id, chunk, failed.Token));
                if (!full)
                    break;
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            await failed.CancelAsync().ConfigureAwait(false);
            await WaitQuietlyAsync(pending).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task StageAndReleaseAsync(BlockBlobClient blob, string id, Chunk chunk, CancellationToken cancellationToken)
    {
        using (chunk)
            await StageAsync(blob, id, chunk, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Response<BlockInfo>> StageAsync(BlockBlobClient blob, string id, Chunk chunk, CancellationToken cancellationToken)
    {
        using var content = chunk.Open();
        return await blob.StageBlockAsync(id, content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private int BlockSizeFor(Stream contents)
    {
        if (!contents.CanSeek)
            return BlockSize;
        long remaining;
        try
        {
            remaining = Math.Max(0, contents.Length - contents.Position);
        }
        catch (NotSupportedException)
        {
            return BlockSize;
        }
        return (int)Math.Min(MaxBlockSize, Math.Max(BlockSize, (remaining + MaxCommittedBlocks - 1) / MaxCommittedBlocks));
    }

    private IOException TooManyBlocks(int blockSize) =>
        new($"The content needs more than {MaxCommittedBlocks} blocks of {blockSize} bytes, Azure's limit for one blob. Write it from a stream that can seek, so the blocks are sized to fit.");

    private static string NewBlockId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(BlockIdBytes));

    private BlockBlobClient TempBlob() =>
        _container.GetBlockBlobClient(string.Join('/', [.. Options.RootDirectorySegments, TempFolder, $"{Guid.NewGuid():N}"]));

    // The temp blob as a path of this provider, which can read it.
    private string TempPath(BlobBaseClient temp) => temp.Name[(Options.RootDirectorySegments.Length == 0 ? 0 : string.Join('/', Options.RootDirectorySegments).Length + 1)..];

    private static BlobRequestConditions? WriteConditions(UpdateFileOptions options) =>
        options.IfMatch is { } ifMatch ? new BlobRequestConditions { IfMatch = new ETag(ifMatch) }
        : options.IfNotExists ? new BlobRequestConditions { IfNoneMatch = ETag.All }
        : null;

    private static bool IsConditionFailure(RequestFailedException exception, UpdateFileOptions options) =>
        exception.Status == 412
        || (options.IfNotExists && Is(exception, BlobErrorCode.BlobAlreadyExists))
        || (options.IfMatch is not null && Is(exception, BlobErrorCode.BlobNotFound));

    private static PreconditionFailedException PreconditionFailed(ResolvedPath path, UpdateFileOptions options, Exception? innerException) =>
        options.IfNotExists
            ? new($"The file '{path}' already exists.", innerException)
            : new($"The file '{path}' changed: its version is no longer '{options.IfMatch}'.", innerException);

    // An append keeps what describes the file; a stored hash would describe the old content, so it goes.
    private static BlobHttpHeaders CarriedHeaders(BlobProperties current, string? contentType) => new()
    {
        ContentType = contentType ?? current.ContentType,
        ContentEncoding = current.ContentEncoding,
        ContentLanguage = current.ContentLanguage,
        ContentDisposition = current.ContentDisposition,
        CacheControl = current.CacheControl,
    };

    // A tier set on the blob stays (a commit would reset it to the account's default); an archived blob is not written.
    private static AccessTier? CarriedTier(BlobProperties current)
    {
        string? tier = current.AccessTier;
        if (current.AccessTierInferred || string.IsNullOrEmpty(tier) || string.Equals(tier, "Archive", StringComparison.OrdinalIgnoreCase))
            return null;
        return new AccessTier(tier);
    }

    // Deletes the blob if it is still the version read. False when it changed or is gone.
    private static async Task<bool> TryDeleteAsync(BlobBaseClient blob, ETag etag, CancellationToken cancellationToken)
    {
        try
        {
            await blob.DeleteAsync(DeleteSnapshotsOption.IncludeSnapshots, new BlobRequestConditions { IfMatch = etag }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 412 || Is(e, BlobErrorCode.BlobNotFound))
        {
            return false;
        }
    }

    private static async Task TryDeleteQuietlyAsync(BlobBaseClient blob, ETag? ifMatch = null)
    {
        try
        {
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, ifMatch is { } etag ? new BlobRequestConditions { IfMatch = etag } : null, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the caller is already done, or already reporting the failure that matters.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task WaitQuietlyAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The first failure is already being thrown; the others only finish.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>A block read into a pooled buffer.</summary>
    private sealed class Chunk : IDisposable
    {
        private byte[]? _buffer;

        private Chunk(byte[]? buffer, int count, bool isFull)
        {
            _buffer = buffer;
            Count = count;
            IsFull = isFull;
        }

        /// <summary>How many bytes it holds.</summary>
        public int Count { get; }

        /// <summary>Whether the read filled it, so the source may hold more.</summary>
        public bool IsFull { get; }

        public static Chunk Empty() => new(null, 0, false);

        public static async Task<Chunk> ReadAsync(Stream source, int size, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                var read = await source.ReadAtLeastAsync(buffer.AsMemory(0, size), size, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                return new Chunk(buffer, read, read == size);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        public MemoryStream Open()
        {
            ObjectDisposedException.ThrowIf(_buffer is null && Count > 0, this);
            return new MemoryStream(_buffer ?? [], 0, Count, writable: false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
