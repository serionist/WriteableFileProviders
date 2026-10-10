using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace WriteableFileProviders.S3;

internal sealed partial class S3Core
{
    private const int DeleteBatchSize = 1000;

    /// <summary>A file (its HEAD), a folder (some key under its prefix), or a missing path. A file wins over a folder of the same name.</summary>
    /// <param name="path">The path, not the root.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IWriteableFileInfo> GetFileInfoAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (await HeadAsync(Key(path), cancellationToken).ConfigureAwait(false) is { } head)
        {
            return new WriteableFileInfo(_owner, path.Path, true, false, head.ContentLength, Utc(head.LastModified),
                etag: head.ETag, contentType: head.Headers.ContentType);
        }
        var prefix = FolderPrefix(path);
        var page = await CallAsync(c => c.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = prefix,
            MaxKeys = 1,
            RequestPayer = Payer,
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        return page.S3Objects is { Count: > 0 }
            ? WriteableFileInfo.Folder(_owner, path.Path)
            : WriteableFileInfo.Missing(_owner, path.Path);
    }

    /// <summary>One level of a folder: its sub-folders (common prefixes) and files. Names the provider would refuse are skipped.</summary>
    /// <param name="path">The folder; may be the root.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public async IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var prefix = FolderPrefix(path);
        string? token = null;
        do
        {
            var continuation = token;
            var page = await CallAsync(c => c.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                Prefix = prefix,
                Delimiter = "/",
                ContinuationToken = continuation,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var folder in page.CommonPrefixes ?? [])
            {
                var name = folder.Length > prefix.Length ? folder[prefix.Length..].TrimEnd('/') : "";
                if (_isAddressable(name))
                    yield return WriteableFileInfo.Folder(_owner, path.Child(name).Path);
            }
            foreach (var item in page.S3Objects ?? [])
            {
                // A folder marker ("dir/") has an empty name here, and a key with an empty segment cannot be addressed.
                var name = item.Key[prefix.Length..];
                if (_isAddressable(name))
                    yield return FileInfo(path.Child(name), item);
            }
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }

    /// <summary>
    /// Every level of a folder in one paged listing. Keys come in UTF-8 binary order, so the keys under a folder are
    /// contiguous: each folder is yielded once, when its first key appears, before its contents.
    /// </summary>
    /// <param name="path">The folder; may be the root.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public async IAsyncEnumerable<IWriteableFileInfo> EnumerateRecursiveAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var prefix = FolderPrefix(path);
        var open = new List<ResolvedPath>();
        string? token = null;
        do
        {
            var continuation = token;
            var page = await CallAsync(c => c.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                Prefix = prefix,
                ContinuationToken = continuation,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var item in page.S3Objects ?? [])
            {
                var segments = item.Key[prefix.Length..].Split('/');
                var folders = segments[..^1];
                var name = segments[^1];
                // A folder marker ("dir/") ends in an empty name: it stands for its folder only.
                if (!folders.All(_isAddressable) || (name.Length > 0 && !_isAddressable(name)))
                    continue;
                var common = 0;
                while (common < open.Count && common < folders.Length && open[common].Name == folders[common])
                    common++;
                open.RemoveRange(common, open.Count - common);
                for (var i = common; i < folders.Length; i++)
                {
                    var folder = (i == 0 ? path : open[i - 1]).Child(folders[i]);
                    open.Add(folder);
                    yield return WriteableFileInfo.Folder(_owner, folder.Path);
                }
                if (name.Length > 0)
                    yield return FileInfo((folders.Length == 0 ? path : open[^1]).Child(name), item);
            }
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }

    /// <summary>Opens a file, or one version of it, as the network stream S3 returns.</summary>
    /// <param name="path">The file.</param>
    /// <param name="versionId">The version, or null for the current one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The file or the version does not exist.</exception>
    public async Task<Stream> ReadAsync(ResolvedPath path, string? versionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var response = await GetAsync(path, new GetObjectRequest
        {
            BucketName = Bucket,
            Key = Key(path),
            VersionId = versionId,
            RequestPayer = Payer,
        }, cancellationToken).ConfigureAwait(false);
        return response.ResponseStream;
    }

    /// <summary>
    /// Reads a byte range with one ranged GET, which S3 checks against <paramref name="ifMatch"/> atomically. Past the
    /// end, or for a length of 0, the stream is empty.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="offset">The first byte.</param>
    /// <param name="length">How many bytes, or null to the end.</param>
    /// <param name="ifMatch">The ETag the file must have, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Stream> ReadRangeAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (length == 0)
        {
            // Nothing to fetch, but the file and its version are still checked.
            var head = await HeadAsync(Key(path), cancellationToken).ConfigureAwait(false) ?? throw NotFound(path);
            if (ifMatch is not null && !string.Equals(head.ETag, ifMatch, StringComparison.Ordinal))
                throw Changed(path, ifMatch, null);
            return Stream.Null;
        }
        var request = new GetObjectRequest
        {
            BucketName = Bucket,
            Key = Key(path),
            ByteRange = length is { } count ? new ByteRange(offset, offset + count - 1) : new ByteRange($"bytes={offset}-"),
            EtagToMatch = ifMatch,
            RequestPayer = Payer,
        };
        try
        {
            return (await GetAsync(path, request, cancellationToken).ConfigureAwait(false)).ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The offset is at or past the end.
            return Stream.Null;
        }
    }

    /// <summary>
    /// Deletes a file, or every key under the folder's prefix (<c>dir/</c>, so a sibling sharing the name's prefix is
    /// never touched), in batches of 1,000, each from a fresh listing.
    /// </summary>
    /// <param name="path">The file or folder, not the root.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether something was deleted.</returns>
    public async Task<bool> DeleteAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (await HeadAsync(Key(path), cancellationToken).ConfigureAwait(false) is not null)
        {
            await DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        var prefix = FolderPrefix(path);
        var deleted = false;
        string? previousFirstKey = null;
        while (true)
        {
            var page = await CallAsync(c => c.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                Prefix = prefix,
                MaxKeys = DeleteBatchSize,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (page.S3Objects is not { Count: > 0 } objects)
                return deleted;
            if (objects[0].Key == previousFirstKey)
                throw new IOException($"The objects under '{path}' could not be deleted: the store still lists them.");
            previousFirstKey = objects[0].Key;
            try
            {
                await CallAsync(c => c.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = Bucket,
                    Quiet = true,
                    Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })],
                    RequestPayer = Payer,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (DeleteObjectsException e) when (e.Response?.DeleteErrors is { Count: > 0 } errors)
            {
                // The SDK throws when the batch reports errors; some objects may be gone, the others are named.
                throw new IOException(
                    $"Failed to delete {errors.Count} object(s) under '{path}'. First: '{errors[0].Key}' {errors[0].Code} {errors[0].Message}", e);
            }
            deleted = true;
        }
    }

    /// <summary>Deletes exactly one key, never a prefix: what a move removes.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task DeleteFileAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        await CallAsync(c => c.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = Bucket,
            Key = Key(path),
            RequestPayer = Payer,
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // HEAD answers 404 for a missing key and for a missing bucket alike; the bucket was checked on first use, and a
    // listing after a 404 names a bucket that went missing since.
    private async Task<GetObjectMetadataResponse?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(c => c.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = Bucket,
                Key = key,
                RequestPayer = Payer,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<GetObjectResponse> GetAsync(ResolvedPath path, GetObjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(c => c.GetObjectAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (IsNotFound(e))
        {
            throw NotFound(path, e);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw Changed(path, request.EtagToMatch, e);
        }
    }

    private WriteableFileInfo FileInfo(ResolvedPath path, S3Object item) =>
        new(_owner, path.Path, true, false, item.Size ?? 0, Utc(item.LastModified), etag: item.ETag);

    private static DateTimeOffset Utc(DateTime? time) =>
        time is { } t ? new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()) : DateTimeOffset.UnixEpoch;

    private static PreconditionFailedException Changed(ResolvedPath path, string? etag, Exception? inner) =>
        new($"The file '{path}' changed: its version is no longer '{etag}'.", inner);
}
