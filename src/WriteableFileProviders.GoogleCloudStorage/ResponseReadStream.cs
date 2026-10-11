namespace WriteableFileProviders.GoogleCloudStorage;

/// <summary>
/// The body of a media download, read forward as it arrives. It owns the HTTP response and disposes it. When the server
/// ignored the range asked for (it sent the whole object), the stream skips to the offset and stops after the length,
/// so the caller still gets the bytes it asked for.
/// </summary>
internal sealed class ResponseReadStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _body;
    private long _skip;
    private long? _remaining;

    /// <summary>Wraps a response's body.</summary>
    /// <param name="response">The response, owned from now on.</param>
    /// <param name="body">Its content stream.</param>
    /// <param name="skip">Bytes to drop first: the offset asked for, when the server ignored the range.</param>
    /// <param name="limit">Bytes to return at most, when the server ignored the range; else null.</param>
    public ResponseReadStream(HttpResponseMessage response, Stream body, long skip, long? limit)
    {
        _response = response;
        _body = body;
        _skip = skip;
        _remaining = limit;
        if (response.Content.Headers.ContentLength is { } contentLength)
        {
            var available = Math.Max(0, contentLength - skip);
            KnownLength = limit is { } max ? Math.Min(max, available) : available;
        }
    }

    /// <summary>How many bytes the stream returns, when the response said.</summary>
    public long? KnownLength { get; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => KnownLength ?? throw new NotSupportedException("The response did not say its length.");

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_skip > 0)
            SkipSync();
        if (_remaining <= 0 || buffer.IsEmpty)
            return 0;
        var read = _body.Read(buffer[..Allowed(buffer.Length)]);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_skip > 0)
            await SkipAsync(cancellationToken).ConfigureAwait(false);
        if (_remaining <= 0 || buffer.IsEmpty)
            return 0;
        var read = await _body.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken).ConfigureAwait(false);
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
        await _body.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _body.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }

    private int Allowed(int count) => _remaining is { } remaining ? (int)Math.Min(count, remaining) : count;

    private void SkipSync()
    {
        var scratch = new byte[(int)Math.Min(_skip, 81920)];
        while (_skip > 0)
        {
            var read = _body.Read(scratch, 0, (int)Math.Min(scratch.Length, _skip));
            if (read == 0)
            {
                _remaining = 0;
                break;
            }
            _skip -= read;
        }
        _skip = 0;
    }

    private async Task SkipAsync(CancellationToken cancellationToken)
    {
        var scratch = new byte[(int)Math.Min(_skip, 81920)];
        while (_skip > 0)
        {
            var read = await _body.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, _skip)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _remaining = 0;
                break;
            }
            _skip -= read;
        }
        _skip = 0;
    }
}
