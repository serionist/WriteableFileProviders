using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace WriteableFileProviders.AzureBlob;

public sealed partial class WriteableAzureBlobFileProvider
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
    /// between is kept, the copy is removed, and <see cref="PreconditionFailedException"/> is thrown. A source Azure
    /// refuses to delete is kept too, and the copy is removed: a refusal of the credential or the permission (401, 403)
    /// throws <see cref="UnauthorizedAccessException"/>, any other (it has snapshots, which are never deleted, or a lease)
    /// <see cref="IOException"/>.
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
            RequestFailedException failure;
            try
            {
                await from.DeleteAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = version }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException e) when (Is(e, BlobErrorCode.BlobNotFound))
            {
                return;
            }
            catch (RequestFailedException e) when (IsConditionNotMet(e))
            {
                // Gone: someone deleted the source after the copy, so the move is complete. Changed: undo the copy.
                if (await GetPropertiesOrNullAsync(from, cancellationToken).ConfigureAwait(false) is null)
                    return;
                failure = e;
            }
            catch (RequestFailedException e) when (e.Status is >= 400 and < 500)
            {
                // A definite refusal (snapshots, a lease, the credential or the permission): the source is as it was. A
                // server or network error is left as it is, since the delete may have happened.
                failure = e;
            }
            var removed = await TryDeleteQuietlyAsync(Blob(destination), copy.ETag).ConfigureAwait(false);
            var copyFate = removed ? $"the copy at '{destination}' was removed" : $"the copy at '{destination}' could not be removed and is still there";
            if (IsConditionNotMet(failure))
                throw new PreconditionFailedException($"The file '{source}' changed while it was moved: it keeps its new content, and {copyFate}.", failure);
            if (failure.Status is 401 or 403)
                throw new UnauthorizedAccessException($"Azure refused to delete '{source}' ({failure.ErrorCode ?? failure.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}): it was not moved, and {copyFate}.", failure);
            throw new IOException(Is(failure, BlobErrorCode.SnapshotsPresent)
                ? $"The file '{source}' has snapshots, which {nameof(WriteableAzureBlobFileProvider)} never deletes: it was not moved, and {copyFate}."
                : $"Azure refused to delete '{source}' ({failure.ErrorCode ?? failure.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}): it was not moved, and {copyFate}.", failure);
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
    /// Copies a blob on the server and waits until the copy is done, at most <see cref="CopyTimeout"/>. Null when a
    /// condition failed (the destination exists or is not the version given, or the source changed): nothing was
    /// copied. Throws <see cref="IOException"/> when the copy failed or timed out; a copy that was making a new blob is
    /// then aborted and what it left removed, but a blob it was overwriting is never deleted.
    /// </summary>
    /// <param name="source">The blob to copy.</param>
    /// <param name="destination">The blob to copy to.</param>
    /// <param name="destinationConditions"><c>IfNoneMatch = *</c> for a new blob, or <c>IfMatch</c> on the blob it replaces.</param>
    /// <param name="sourceVersion">The source's version to copy, or null for whatever it holds.</param>
    /// <param name="cancellationToken">Cancels the call, aborting a copy that started.</param>
    /// <param name="started">Set once the copy started, from when it may have overwritten the destination.</param>
    private async Task<BlobProperties?> TryCopyAsync(BlobBaseClient source, BlobBaseClient destination, BlobRequestConditions destinationConditions, ETag? sourceVersion, CancellationToken cancellationToken, StrongBox<bool>? started = null)
    {
        var createsDestination = destinationConditions.IfNoneMatch == ETag.All;
        CopyFromUriOperation operation;
        try
        {
            operation = await destination.StartCopyFromUriAsync(source.Uri, new BlobCopyFromUriOptions
            {
                DestinationConditions = destinationConditions,
                SourceConditions = sourceVersion is { } version ? new BlobRequestConditions { IfMatch = version } : null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (IsConditionNotMet(e) || Is(e, BlobErrorCode.BlobAlreadyExists) || Is(e, BlobErrorCode.InvalidBlobType))
        {
            return null;
        }
        started?.Value = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CopyTimeout);
        try
        {
            await operation.WaitForCompletionAsync(CopyPollInterval, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await TryAbortCopyAsync(destination, operation.Id, createsDestination).ConfigureAwait(false);
            throw new IOException($"Copying '{source.Name}' to '{destination.Name}' did not complete within {CopyTimeout}; it was aborted.", e);
        }
        catch (OperationCanceledException)
        {
            await TryAbortCopyAsync(destination, operation.Id, createsDestination).ConfigureAwait(false);
            throw;
        }
        // Waiting ends without an error when the copy failed or was aborted: the destination says how it ended.
        var copied = (await destination.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        if (copied.CopyId == operation.Id && copied.BlobCopyStatus == CopyStatus.Success)
            return copied;
        if (createsDestination && copied.CopyId == operation.Id)
            await TryDeleteQuietlyAsync(destination, copied.ETag).ConfigureAwait(false);
        throw new IOException($"Copying '{source.Name}' to '{destination.Name}' did not complete: {copied.BlobCopyStatus} {copied.CopyStatusDescription}".TrimEnd());
    }

    private static async Task TryAbortCopyAsync(BlobBaseClient destination, string copyId, bool deleteDestination)
    {
        try
        {
            await destination.AbortCopyFromUriAsync(copyId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            // An aborted copy leaves an empty blob: removed when the copy was making it, kept when it is the caller's file.
            if (deleteDestination)
                await destination.DeleteIfExistsAsync(DeleteSnapshotsOption.None, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the copy may have finished already; the failure is what the caller sees.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
