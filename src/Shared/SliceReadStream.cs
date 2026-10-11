namespace WriteableFileProviders;

/// <summary>
/// A forward-only read of part of another stream, the body of a download: it skips <c>skip</c> bytes first, then returns
/// at most <c>limit</c> bytes (all the rest when null), and when disposed it disposes the body, then <c>owner</c> (the
/// HTTP response the body came from). A ranged read uses it whether or not the server honoured the range: a server that
/// sent the whole file is skipped into. Source shared by Common and the providers that download through HTTP; each
/// compiles its own internal copy.
/// </summary>
/// <param name="body">The stream to read, owned from now on.</param>
/// <param name="skip">How many bytes to drop first: by seeking when the body can seek, else by reading.</param>
/// <param name="limit">How many bytes to return at most, or null for the rest of the body.</param>
/// <param name="owner">What else to dispose with the body, such as its HTTP response, or null.</param>
/// <param name="knownLength">How many bytes the stream returns, when the caller knows (a response's length), or null.</param>
internal sealed class SliceReadStream(Stream body, long skip, long? limit, IDisposable? owner = null, long? knownLength = null) : Stream
{
    private const int SkipBufferSize = 81920;
    private long _skip = Math.Max(0, skip);
    private long? _remaining = limit;
    private bool _disposed;

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    /// <summary>The number of bytes the stream returns, when it was given; otherwise <see cref="NotSupportedException"/>.</summary>
    public override long Length => knownLength ?? throw new NotSupportedException("The stream does not know its length.");

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Skips now rather than on the first read, so a failure shows here. Does nothing once skipped.</summary>
    /// <param name="cancellationToken">Cancels the skip.</param>
    public async Task SkipAsync(CancellationToken cancellationToken)
    {
        if (_skip == 0)
            return;
        if (body.CanSeek)
        {
            SkipBySeeking();
            return;
        }
        var scratch = new byte[(int)Math.Min(SkipBufferSize, _skip)];
        while (_skip > 0)
        {
            var read = await body.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, _skip)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                EndedWhileSkipping();
                return;
            }
            _skip -= read;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_skip > 0)
            Skip();
        var allowed = Allowed(buffer.Length);
        if (allowed == 0)
            return 0;
        var read = body.Read(buffer[..allowed]);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_skip > 0)
            await SkipAsync(cancellationToken).ConfigureAwait(false);
        var allowed = Allowed(buffer.Length);
        if (allowed == 0)
            return 0;
        var read = await body.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                await body.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                owner?.Dispose();
            }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                body.Dispose();
            }
            finally
            {
                owner?.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private void Skip()
    {
        if (body.CanSeek)
        {
            SkipBySeeking();
            return;
        }
        var scratch = new byte[(int)Math.Min(SkipBufferSize, _skip)];
        while (_skip > 0)
        {
            var read = body.Read(scratch, 0, (int)Math.Min(scratch.Length, _skip));
            if (read == 0)
            {
                EndedWhileSkipping();
                return;
            }
            _skip -= read;
        }
    }

    private void SkipBySeeking()
    {
        var left = body.Length - body.Position;
        if (_skip >= left)
        {
            body.Position = body.Length;
            _remaining = 0;
        }
        else
        {
            body.Position += _skip;
        }
        _skip = 0;
    }

    // The body ended before the offset: there is nothing to return.
    private void EndedWhileSkipping()
    {
        _skip = 0;
        _remaining = 0;
    }

    private int Allowed(int requested) =>
        requested == 0 ? 0 : _remaining is { } remaining ? (int)Math.Min(requested, Math.Max(0, remaining)) : requested;
}
