using Azure;
using Azure.Identity;

namespace WriteableFileProviders.AzureBlob;

/// <summary>
/// The SDK's seekable blob stream (<c>OpenReadAsync</c>), with its errors in the contract's terms: a read after the file
/// changed throws <see cref="PreconditionFailedException"/>, and after it was deleted <see cref="FileNotFoundException"/>,
/// both <see cref="IOException"/>s; a range read whose sign-in finds no credential throws
/// <see cref="InvalidOperationException"/>, as every other member of the provider does.
/// </summary>
/// <param name="inner">The SDK's stream, which this one disposes.</param>
/// <param name="path">The file's path, for the messages.</param>
/// <param name="noCredential">The provider's exception for a sign-in that finds no credential.</param>
internal sealed class BlobReadStream(Stream inner, string path, Func<CredentialUnavailableException, InvalidOperationException> noCredential) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (RequestFailedException e) when (e.Status == 404 || WriteableAzureBlobFileProvider.IsConditionNotMet(e))
        {
            throw Translate(e);
        }
        catch (CredentialUnavailableException e)
        {
            throw noCredential(e);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 404 || WriteableAzureBlobFileProvider.IsConditionNotMet(e))
        {
            throw Translate(e);
        }
        catch (CredentialUnavailableException e)
        {
            throw noCredential(e);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("The stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream is read-only.");

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    private IOException Translate(RequestFailedException e) => e.Status == 404
        ? new FileNotFoundException($"The file '{path}' was deleted while it was read.", path, e)
        : new PreconditionFailedException($"The file '{path}' changed while it was read.", e);
}
