using Microsoft.AspNetCore.Http;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// A request body that throws a 413 <see cref="BadHttpRequestException"/> once more than <paramref name="limit"/> bytes
/// have been read from it, whatever the server's own limit, so an upload without a <c>Content-Length</c> is cut off too.
/// It leaves the body it wraps open: the server owns it.
/// </summary>
/// <param name="inner">The request body.</param>
/// <param name="limit">The most bytes it may hold.</param>
internal sealed class UploadLimitStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Counted(int read)
    {
        _read += read;
        if (_read > limit)
            throw new BadHttpRequestException($"The upload is larger than the gateway's limit of {limit} bytes.", StatusCodes.Status413PayloadTooLarge);
        return read;
    }
}
