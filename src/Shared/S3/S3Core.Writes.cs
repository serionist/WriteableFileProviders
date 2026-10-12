using System.Buffers;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace WriteableFileProviders.S3;

internal sealed partial class S3Core
{
    /// <summary>What a file is stored as when the caller gives no content type: the same on every provider.</summary>
    public const string DefaultContentType = "application/octet-stream";

    private const int FirstReadSize = 81_920;

    /// <summary>
    /// Writes a file from <paramref name="contents"/>'s position to its end, never seeking, rewinding or disposing it. Up
    /// to <see cref="PartSize"/> goes in one PUT, anything larger in a multipart upload; either way the store replaces the
    /// object only when the whole content arrived. <see cref="UpdateFileOptions.IfMatch"/> and
    /// <see cref="UpdateFileOptions.IfNotExists"/> go to the store with the write, which AWS checks atomically.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content.</param>
    /// <param name="options">The content type and the conditions; the mode is overwrite.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task WriteAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        long? remaining = contents.CanSeek ? Math.Max(0, contents.Length - contents.Position) : null;
        var partSize = PartSizeFor(remaining, 1);
        // The buffer holds one part, and one byte past it tells whether more follows: up to exactly PartSize is one
        // request. A known short content is read in one buffer of its size; an unknown one grows its buffer as it arrives.
        using var buffer = new PartBuffer((int)Math.Min(partSize, remaining is { } n ? Math.Max(n, 1) : FirstReadSize));
        await buffer.FillAsync(contents, partSize, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!buffer.HasMore)
                await PutAsync(client, path, buffer, options, cancellationToken).ConfigureAwait(false);
            else
                await UploadInPartsAsync(client, path, contents, remaining, buffer, options, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (IsConditionFailure(e) || (options.IfMatch is not null && IsNotFound(e)))
        {
            // 412: the condition failed; 409: a concurrent operation on the key won; 404 with If-Match: none to match.
            if (!options.IfNotExists)
                throw Changed(path, options.IfMatch, e);
            // A 409 also answers a write that a concurrent delete overtook, so the file need not be there.
            if (e.StatusCode == HttpStatusCode.PreconditionFailed || await ExistsAfterConflictAsync(path).ConfigureAwait(false))
                throw new FileAlreadyExistsException($"The file '{path}' already exists.", e);
            throw new PreconditionFailedException($"Writing '{path}' conflicted with a concurrent operation on it, and nothing was written; the write can be retried.", e);
        }
    }

    /// <summary>
    /// Copies a file on the server, refusing an existing destination (<c>If-None-Match: *</c>, atomic on AWS) and a
    /// source that changed since it was checked. Above <see cref="MaxSingleCopySize"/> it copies in parts.
    /// </summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ETag of the source that was copied, and the copy's own.</returns>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="FileExistsException">The destination exists.</exception>
    public async Task<CopyResult> CopyAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var head = await HeadAsync(Key(source), cancellationToken).ConfigureAwait(false)
            ?? throw await NotFoundAsync(source, cancellationToken).ConfigureAwait(false);
        try
        {
            string? copy;
            if (head.ContentLength <= MaxSingleCopySize)
            {
                copy = (await CallAsync(c => c.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = Bucket,
                    SourceKey = Key(source),
                    DestinationBucket = Bucket,
                    DestinationKey = Key(destination),
                    ETagToMatch = head.ETag,
                    IfNoneMatch = "*",
                    StorageClass = _settings.StorageClass,
                    RequestPayer = Payer,
                }, cancellationToken), cancellationToken).ConfigureAwait(false)).ETag;
            }
            else
            {
                copy = await CopyInPartsAsync(source, destination, head, cancellationToken).ConfigureAwait(false);
            }
            return new CopyResult(head.ETag, copy);
        }
        catch (AmazonS3Exception e) when (IsNotFound(e))
        {
            throw NotFound(source, e);
        }
        catch (AmazonS3Exception e) when (IsConditionFailure(e))
        {
            if (await HeadAsync(Key(destination), CancellationToken.None).ConfigureAwait(false) is not null)
                throw new FileExistsException($"The destination '{destination}' already exists.", e);
            throw new PreconditionFailedException($"The file '{source}' changed while it was copied.", e);
        }
    }

    /// <summary>
    /// Moves a file: copies it (see <see cref="CopyAsync"/>), then deletes exactly the source key with <c>If-Match</c> on
    /// the version that was copied.
    /// <list type="bullet">
    /// <item>A source deleted meanwhile completes the move: the copy holds the content.</item>
    /// <item>
    /// A source changed meanwhile undoes it: the copy is deleted with <c>If-Match</c> on its own version, and the move
    /// throws <see cref="PreconditionFailedException"/>, so no change is lost. When the copy was itself overwritten
    /// meanwhile, it is left alone, and the exception says so.
    /// </item>
    /// </list>
    /// AWS checks both conditions atomically; S3-compatible servers may ignore them on a delete (MinIO does), so there the
    /// versions are also checked just before each delete, which is not atomic.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes; it does not exist.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MoveAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var copy = await CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        Exception changed;
        try
        {
            if (!ChecksAtomically)
            {
                var head = await HeadAsync(Key(source), cancellationToken).ConfigureAwait(false);
                if (head is null)
                    return;
                if (head.ETag != copy.SourceETag)
                    throw Changed(source, copy.SourceETag, null);
            }
            await CallAsync(c => c.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Bucket,
                Key = Key(source),
                IfMatch = copy.SourceETag,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (AmazonS3Exception e) when (IsNotFound(e))
        {
            // Deleted by another writer since the copy: the move is complete, as on the other providers.
            return;
        }
        catch (Exception e) when (e is PreconditionFailedException || (e is AmazonS3Exception s3 && IsConditionFailure(s3)))
        {
            changed = e;
        }
        await UndoCopyAsync(source, destination, copy.CopyETag, changed).ConfigureAwait(false);
    }

    // The source changed after it was copied: delete the copy, but only while it is still the copy.
    private async Task UndoCopyAsync(ResolvedPath source, ResolvedPath destination, string? copyETag, Exception changed)
    {
        Exception? overwritten = null;
        try
        {
            if (!ChecksAtomically && await HeadAsync(Key(destination), CancellationToken.None).ConfigureAwait(false) is { } head)
            {
                if (copyETag is not null && head.ETag != copyETag)
                    overwritten = Changed(destination, copyETag, null);
            }
            if (overwritten is null)
            {
                await CallAsync(c => c.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = Bucket,
                    Key = Key(destination),
                    IfMatch = copyETag,
                    RequestPayer = Payer,
                }, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (AmazonS3Exception e) when (IsConditionFailure(e))
        {
            overwritten = e;
        }
        catch (AmazonS3Exception e) when (IsNotFound(e))
        {
            // The copy is already gone: nothing to undo.
        }
#pragma warning disable CA1031 // Any failure leaves the copy behind, and the caller must learn where it is.
        catch (Exception rollback)
#pragma warning restore CA1031
        {
            throw new IOException(
                $"The file '{source}' changed while it was moved, and its copy '{destination}' could not be removed.", new AggregateException(changed, rollback));
        }
        if (overwritten is not null)
        {
            throw new PreconditionFailedException(
                $"The file '{source}' changed while it was moved, so it was not moved; '{destination}' now holds another write and was left as it is.",
                new AggregateException(changed, overwritten));
        }
        throw new PreconditionFailedException($"The file '{source}' changed while it was moved; the move was undone.", changed);
    }

    /// <summary>What a copy copied: the source's version it matched, and the copy's own version, if the store said.</summary>
    /// <param name="SourceETag">The source's ETag.</param>
    /// <param name="CopyETag">The copy's ETag.</param>
    public readonly record struct CopyResult(string SourceETag, string? CopyETag);

    // 412 Precondition Failed, or the 409 S3 answers when a concurrent operation on the same key won.
    private static bool IsConditionFailure(AmazonS3Exception e) =>
        e.StatusCode == HttpStatusCode.PreconditionFailed || e.ErrorCode == "ConditionalRequestConflict";

    // Whether a file is at the path after a create lost a conflict; false when the store cannot say, so the caller
    // reports the conflict rather than a file that may not be there.
    private async Task<bool> ExistsAfterConflictAsync(ResolvedPath path)
    {
        try
        {
            return await HeadAsync(Key(path), CancellationToken.None).ConfigureAwait(false) is not null;
        }
#pragma warning disable CA1031 // The write's own failure is the one to report; this only picks its type.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task PutAsync(IAmazonS3 client, ResolvedPath path, PartBuffer buffer, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        using var body = buffer.Content();
        await CallAsync(_ => client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = Key(path),
            InputStream = body,
            ContentType = options.ContentType ?? DefaultContentType,
            StorageClass = _settings.StorageClass,
            IfMatch = options.IfMatch,
            IfNoneMatch = options.IfNotExists ? "*" : null,
            RequestPayer = Payer,
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadInPartsAsync(IAmazonS3 client, ResolvedPath path, Stream contents, long? remaining, PartBuffer buffer,
        UpdateFileOptions options, CancellationToken cancellationToken)
    {
        var key = Key(path);
        var upload = await CallAsync(_ => client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = key,
            ContentType = options.ContentType ?? DefaultContentType,
            StorageClass = _settings.StorageClass,
            RequestPayer = Payer,
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        var parts = new List<PartETag>();
        try
        {
            // The buffer holds a part and knows whether a byte follows it: send the part, then, only when more follows,
            // start the next part with that byte; a part past the limit fails only when it has data.
            var partNumber = 1;
            while (true)
            {
                parts.Add(await UploadPartAsync(client, key, upload.UploadId, partNumber, buffer, cancellationToken).ConfigureAwait(false));
                if (!buffer.HasMore)
                    break;
                var partSize = PartSizeFor(remaining, ++partNumber);
                if (partNumber > MaxPartCount)
                    throw new IOException($"The file '{path}' is too large: S3 takes at most {MaxPartCount} parts, of {partSize} bytes here.");
                buffer.StartNextPart();
                await buffer.FillAsync(contents, partSize, cancellationToken).ConfigureAwait(false);
            }
            await CallAsync(_ => client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = upload.UploadId,
                PartETags = parts,
                IfMatch = options.IfMatch,
                IfNoneMatch = options.IfNotExists ? "*" : null,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync(client, key, upload.UploadId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PartETag> UploadPartAsync(IAmazonS3 client, string key, string uploadId, int partNumber, PartBuffer buffer,
        CancellationToken cancellationToken)
    {
        // The part is in memory, so the SDK can rewind it to retry; the caller's stream is never seeked.
        using var body = buffer.Content();
        var response = await CallAsync(_ => client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = Bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            InputStream = body,
            RequestPayer = Payer,
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new PartETag { PartNumber = partNumber, ETag = response.ETag };
    }

    private async Task<string?> CopyInPartsAsync(ResolvedPath source, ResolvedPath destination, GetObjectMetadataResponse head, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var length = head.ContentLength;
        var partSize = Math.Max(CopyPartSize, (length + MaxPartCount - 1) / MaxPartCount);
        var key = Key(destination);
        // A single CopyObject keeps the source's headers and metadata; a copy in parts carries them over itself. Tags are
        // left out: reading them takes a call of their own.
        var initiate = new InitiateMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = key,
            ContentType = head.Headers.ContentType ?? DefaultContentType,
            StorageClass = _settings.StorageClass,
            RequestPayer = Payer,
        };
        initiate.Headers.CacheControl = head.Headers.CacheControl;
        initiate.Headers.ContentDisposition = head.Headers.ContentDisposition;
        initiate.Headers.ContentEncoding = head.Headers.ContentEncoding;
        initiate.Headers.ContentLanguage = head.Headers.ContentLanguage;
        // The SDK gives a HEAD's Expires as text; its typed form stays empty.
        if (!string.IsNullOrEmpty(head.ExpiresString))
            initiate.Headers["Expires"] = head.ExpiresString;
        foreach (var name in head.Metadata.Keys)
            initiate.Metadata[name] = head.Metadata[name];
        var upload = await CallAsync(_ => client.InitiateMultipartUploadAsync(initiate, cancellationToken), cancellationToken).ConfigureAwait(false);
        try
        {
            var parts = new List<PartETag>();
            for (var (offset, partNumber) = (0L, 1); offset < length; offset += partSize, partNumber++)
            {
                var part = await CallAsync(_ => client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = Bucket,
                    SourceKey = Key(source),
                    DestinationBucket = Bucket,
                    DestinationKey = key,
                    UploadId = upload.UploadId,
                    PartNumber = partNumber,
                    FirstByte = offset,
                    LastByte = Math.Min(offset + partSize, length) - 1,
                    ETagToMatch = [head.ETag],
                    RequestPayer = Payer,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag { PartNumber = partNumber, ETag = part.ETag });
            }
            return (await CallAsync(_ => client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = upload.UploadId,
                PartETags = parts,
                IfNoneMatch = "*",
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false)).ETag;
        }
        catch
        {
            await AbortAsync(client, key, upload.UploadId).ConfigureAwait(false);
            throw;
        }
    }

    // Best effort: the failure being reported is the one that matters, and S3 lifecycle rules can clean what is left.
    private async Task AbortAsync(IAmazonS3 client, string key, string uploadId)
    {
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = uploadId,
                RequestPayer = Payer,
            }, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort, see above.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // Parts of a known length are equal and fit in 10,000; with an unknown length they grow by PartSize every 1,000 parts,
    // so 10,000 parts hold 55,000 × PartSize (430 GiB at 8 MiB).
    private int PartSizeFor(long? length, int partNumber)
    {
        var size = length is { } n
            ? Math.Max(PartSize, (n + MaxPartCount - 1) / MaxPartCount)
            : PartSize * (1 + ((partNumber - 1) / 1000));
        return (int)Math.Min(size, Array.MaxLength);
    }

    /// <summary>
    /// One part's bytes, in an array rented from the shared pool and returned on dispose, and whether a byte follows the
    /// part. That lookahead byte is kept apart, so the array is never larger than a part.
    /// </summary>
    private sealed class PartBuffer(int initialSize) : IDisposable
    {
        private readonly byte[] _next = new byte[1];
        private byte[] _array = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialSize));
        private bool _ended;

        /// <summary>How many bytes the part holds.</summary>
        public int Count { get; private set; }

        /// <summary>Whether the stream has a byte past the part.</summary>
        public bool HasMore { get; private set; }

        /// <summary>The part, as a read-only stream.</summary>
        public MemoryStream Content() => new(_array, 0, Count, writable: false);

        /// <summary>Empties the part and starts the next one with the byte that followed it.</summary>
        public void StartNextPart()
        {
            _array[0] = _next[0];
            Count = 1;
            HasMore = false;
        }

        /// <summary>
        /// Reads until the part holds <paramref name="limit"/> bytes or the stream ends, after what it already holds,
        /// renting a larger array as needed; then reads one byte more to learn whether the stream goes on.
        /// </summary>
        public async Task FillAsync(Stream source, int limit, CancellationToken cancellationToken)
        {
            while (Count < limit && !_ended)
            {
                if (Count == _array.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(limit, (long)_array.Length * 2));
                    _array.AsSpan(0, Count).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(_array);
                    _array = larger;
                }
                var read = await source.ReadAsync(_array.AsMemory(Count, Math.Min(_array.Length, limit) - Count), cancellationToken).ConfigureAwait(false);
                _ended = read == 0;
                Count += read;
            }
            if (!_ended)
                HasMore = await source.ReadAsync(_next, cancellationToken).ConfigureAwait(false) == 1;
            _ended |= !HasMore;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_array);
    }
}
