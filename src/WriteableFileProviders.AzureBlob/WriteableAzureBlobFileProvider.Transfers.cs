using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace WriteableFileProviders.AzureBlob;

public partial class WriteableAzureBlobFileProvider
{
    /// <summary>
    /// Copies on the server, on the condition that the destination does not exist (atomic), from the version of the
    /// source read just before; a source that changes in between is read again.
    /// </summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GuardAsync(() => CopyBlobAsync(source, destination, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies on the server, then deletes the source only if it is still the version copied. A source that changed in
    /// between is kept, the copy is removed, and <see cref="PreconditionFailedException"/> is thrown.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected override async Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GuardAsync(async () =>
        {
            var (version, copy) = await CopyBlobAsync(source, destination, cancellationToken).ConfigureAwait(false);
            var from = Blob(source);
            if (await TryDeleteAsync(from, version, cancellationToken).ConfigureAwait(false))
                return;
            // Gone: someone deleted the source after the copy, so the move is complete. Changed: undo the copy.
            if (await GetPropertiesOrNullAsync(from, cancellationToken).ConfigureAwait(false) is null)
                return;
            await TryDeleteQuietlyAsync(Blob(destination), copy.ETag).ConfigureAwait(false);
            throw new PreconditionFailedException($"The file '{source}' changed while it was moved: it keeps its new content, and the copy at '{destination}' was removed.");
        }).ConfigureAwait(false);
    }

    private async Task<(ETag Version, BlobProperties Copy)> CopyBlobAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        var from = Blob(source);
        var to = Blob(destination);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await GetPropertiesOrNullAsync(from, cancellationToken).ConfigureAwait(false) ?? throw NotFound(source);
            BlobProperties? copy;
            try
            {
                copy = await TryCopyAsync(from, to, new BlobRequestConditions { IfNoneMatch = ETag.All }, current.ETag, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (Is(e, BlobErrorCode.BlobNotFound) || (Is(e, BlobErrorCode.CannotVerifyCopySource) && e.Status == 404))
            {
                throw NotFound(source, e);
            }
            if (copy is not null)
                return (current.ETag, copy);
            // A condition failed: the destination exists, or the source changed since it was read.
            if (await GetPropertiesOrNullAsync(to, cancellationToken).ConfigureAwait(false) is not null)
                throw new FileExistsException($"The destination '{destination}' already exists.");
        }
        throw new PreconditionFailedException($"The file '{source}' kept changing while it was copied; it was not copied after {MaxAttempts} attempts.");
    }

    /// <summary>
    /// Copies a blob on the server and waits until the copy is done. Null when a condition failed (the destination exists
    /// or is not the version given, or the source changed); throws <see cref="IOException"/> when the copy itself failed,
    /// after removing what it left at the destination.
    /// </summary>
    private async Task<BlobProperties?> TryCopyAsync(BlobBaseClient source, BlobBaseClient destination, BlobRequestConditions destinationConditions, ETag? sourceVersion, CancellationToken cancellationToken)
    {
        CopyFromUriOperation operation;
        try
        {
            operation = await destination.StartCopyFromUriAsync(source.Uri, new BlobCopyFromUriOptions
            {
                DestinationConditions = destinationConditions,
                SourceConditions = sourceVersion is { } version ? new BlobRequestConditions { IfMatch = version } : null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 412 || Is(e, BlobErrorCode.BlobAlreadyExists) || Is(e, BlobErrorCode.InvalidBlobType))
        {
            return null;
        }

        try
        {
            await operation.WaitForCompletionAsync(CopyPollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryAbortCopyAsync(destination, operation.Id).ConfigureAwait(false);
            throw;
        }
        // Waiting ends without an error when the copy failed or was aborted: the destination says how it ended.
        var copied = (await destination.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        if (copied.CopyId == operation.Id && copied.BlobCopyStatus == CopyStatus.Success)
            return copied;
        if (copied.CopyId == operation.Id)
            await TryDeleteQuietlyAsync(destination, copied.ETag).ConfigureAwait(false);
        throw new IOException($"Copying '{source.Name}' to '{destination.Name}' did not complete: {copied.BlobCopyStatus} {copied.CopyStatusDescription}".TrimEnd());
    }

    private static async Task TryAbortCopyAsync(BlobBaseClient destination, string copyId)
    {
        try
        {
            await destination.AbortCopyFromUriAsync(copyId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await destination.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the copy may have finished already; the cancellation is what the caller sees.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
