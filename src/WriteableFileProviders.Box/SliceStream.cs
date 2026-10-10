namespace WriteableFileProviders.Box;

/// <summary>
/// A read-only view of a download: it skips <c>skip</c> bytes on its first read, then yields at most <c>count</c> bytes,
/// and disposes the download. It serves a ranged read whether or not the server honoured the range.
/// </summary>
/// <param name="inner">The download.</param>
/// <param name="skip">How many bytes to drop first: 0 when the server sent the range, the offset when it sent the whole file.</param>
/// <param name="count">How many bytes to yield at most.</param>
internal sealed class SliceStream(Stream inner, long skip, long count) : Stream
{
    private const int SkipBufferSize = 81920;
    private long _skip = skip;
    private long _remaining = count;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_skip > 0)
        {
            var scratch = new byte[(int)Math.Min(SkipBufferSize, _skip)];
            while (_skip > 0)
            {
                var skipped = inner.Read(scratch, 0, (int)Math.Min(scratch.Length, _skip));
                if (skipped == 0)
                    return 0;
                _skip -= skipped;
            }
        }
        if (_remaining <= 0 || buffer.IsEmpty)
            return 0;
        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_skip > 0)
        {
            var scratch = new byte[(int)Math.Min(SkipBufferSize, _skip)];
            while (_skip > 0)
            {
                var skipped = await inner.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, _skip)), cancellationToken).ConfigureAwait(false);
                if (skipped == 0)
                    return 0;
                _skip -= skipped;
            }
        }
        if (_remaining <= 0 || buffer.IsEmpty)
            return 0;
        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
