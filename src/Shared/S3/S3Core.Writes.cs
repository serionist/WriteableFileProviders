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
        // One byte past the part tells whether more follows: up to exactly PartSize is one request. A known short content
        // is read in one buffer of its size; an unknown one grows its buffer as it arrives.
        using var buffer = new PartBuffer((int)Math.Min(partSize + 1L, remaining is { } n ? n + 1 : FirstReadSize));
        await buffer.FillAsync(contents, partSize + 1, cancellationToken).ConfigureAwait(false);
        try
        {
            if (buffer.Count <= partSize)
                await PutAsync(client, path, buffer, options, cancellationToken).ConfigureAwait(false);
            else
                await UploadInPartsAsync(client, path, contents, remaining, buffer, options, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (IsConditionFailure(e) || (options.IfMatch is not null && IsNotFound(e)))
        {
            // 412: the condition failed; 409: a concurrent conditional write won; 404 with If-Match: no file to match.
            throw options.IfNotExists
                ? new PreconditionFailedException($"The file '{path}' already exists.", e)
                : Changed(path, options.IfMatch, e);
        }
    }

    /// <summary>
    /// Copies a file on the server, refusing an existing destination (<c>If-None-Match: *</c>, atomic on AWS) and a
    /// source that changed since it was checked. Above <see cref="MaxSingleCopySize"/> it copies in parts.
    /// </summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ETag of the source that was copied.</returns>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="FileExistsException">The destination exists.</exception>
    public async Task<string> CopyAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var head = await HeadAsync(Key(source), cancellationToken).ConfigureAwait(false)
            ?? throw await NotFoundAsync(source, cancellationToken).ConfigureAwait(false);
        try
        {
            if (head.ContentLength <= MaxSingleCopySize)
            {
                await CallAsync(c => c.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = Bucket,
                    SourceKey = Key(source),
                    DestinationBucket = Bucket,
                    DestinationKey = Key(destination),
                    ETagToMatch = head.ETag,
                    IfNoneMatch = "*",
                    StorageClass = _settings.StorageClass,
                    RequestPayer = Payer,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyInPartsAsync(source, destination, head, cancellationToken).ConfigureAwait(false);
            }
            return head.ETag;
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
    /// the version that was copied. When the source changed meanwhile, the copy is removed and the move throws
    /// <see cref="PreconditionFailedException"/>, so no change is lost. AWS checks the condition atomically; S3-compatible
    /// servers may ignore it on a delete (MinIO does), so there the version is also checked just before the delete, which
    /// is not atomic.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes; it does not exist.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MoveAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var etag = await CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ChecksAtomically && (await HeadAsync(Key(source), cancellationToken).ConfigureAwait(false))?.ETag != etag)
                throw Changed(source, etag, null);
            await CallAsync(c => c.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Bucket,
                Key = Key(source),
                IfMatch = etag,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is PreconditionFailedException || (e is AmazonS3Exception s3 && (IsConditionFailure(s3) || IsNotFound(s3))))
        {
            // The source changed or went after it was copied: take the copy back rather than lose that change.
            try
            {
                await DeleteFileAsync(destination, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollback) when (rollback is AmazonS3Exception or InvalidOperationException or IOException)
            {
                throw new IOException(
                    $"The file '{source}' changed while it was moved, and its copy '{destination}' could not be removed.", new AggregateException(e, rollback));
            }
            throw new PreconditionFailedException($"The file '{source}' changed while it was moved; the move was undone.", e);
        }
    }

    // 412 Precondition Failed, or the 409 S3 answers when a concurrent conditional write to the same key won.
    private static bool IsConditionFailure(AmazonS3Exception e) =>
        e.StatusCode == HttpStatusCode.PreconditionFailed || e.ErrorCode == "ConditionalRequestConflict";

    private async Task PutAsync(IAmazonS3 client, ResolvedPath path, PartBuffer buffer, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        using var body = buffer.Content(buffer.Count);
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
            // The buffer holds the part and, when more follows, its first byte: send the part, keep that byte, and read the
            // next part (and its own lookahead byte) before deciding whether there is one.
            var partNumber = 1;
            var partSize = PartSizeFor(remaining, partNumber);
            while (true)
            {
                var size = Math.Min(buffer.Count, partSize);
                parts.Add(await UploadPartAsync(client, key, upload.UploadId, partNumber, buffer, size, cancellationToken).ConfigureAwait(false));
                buffer.Consume(size);
                partSize = PartSizeFor(remaining, ++partNumber);
                if (await buffer.FillAsync(contents, partSize + 1, cancellationToken).ConfigureAwait(false) == 0)
                    break;
                if (partNumber > MaxPartCount)
                    throw new IOException($"The file '{path}' is too large: S3 takes at most {MaxPartCount} parts, of {partSize} bytes here.");
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

    private async Task<PartETag> UploadPartAsync(IAmazonS3 client, string key, string uploadId, int partNumber, PartBuffer buffer, int size,
        CancellationToken cancellationToken)
    {
        // The part is in memory, so the SDK can rewind it to retry; the caller's stream is never seeked.
        using var body = buffer.Content(size);
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

    private async Task CopyInPartsAsync(ResolvedPath source, ResolvedPath destination, GetObjectMetadataResponse head, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var length = head.ContentLength;
        var partSize = Math.Max(CopyPartSize, (length + MaxPartCount - 1) / MaxPartCount);
        var key = Key(destination);
        // A single CopyObject keeps the source's headers and metadata; a copy in parts carries them over itself.
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
            await CallAsync(_ => client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = upload.UploadId,
                PartETags = parts,
                IfNoneMatch = "*",
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
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
        // One byte below the largest array, for the lookahead byte.
        return (int)Math.Min(size, Array.MaxLength - 1);
    }

    /// <summary>One part's bytes, in an array rented from the shared pool and returned on dispose.</summary>
    private sealed class PartBuffer(int initialSize) : IDisposable
    {
        private byte[] _array = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialSize));
        private bool _ended;

        /// <summary>How many bytes the buffer holds.</summary>
        public int Count { get; private set; }

        /// <summary>The first <paramref name="length"/> bytes, as a read-only stream.</summary>
        public MemoryStream Content(int length) => new(_array, 0, length, writable: false);

        /// <summary>Drops the first <paramref name="length"/> bytes, keeping the rest at the start.</summary>
        public void Consume(int length)
        {
            _array.AsSpan(length, Count - length).CopyTo(_array);
            Count -= length;
        }

        /// <summary>
        /// Reads until the buffer holds <paramref name="limit"/> bytes or the stream ends, after what it already holds,
        /// renting a larger array as needed.
        /// </summary>
        public async Task<int> FillAsync(Stream source, int limit, CancellationToken cancellationToken)
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
            return Count;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_array);
    }
}
