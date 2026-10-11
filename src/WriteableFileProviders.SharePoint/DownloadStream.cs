namespace WriteableFileProviders.SharePoint;

/// <summary>
/// The body of a download, streamed: it reads at most <c>limit</c> bytes and, when disposed, releases the HTTP response
/// it came from.
/// </summary>
/// <param name="response">The response whose body this is.</param>
/// <param name="body">The response's body stream.</param>
/// <param name="limit">How many bytes to read at most, or null for the whole body.</param>
internal sealed class DownloadStream(HttpResponseMessage response, Stream body, long? limit) : Stream
{
    private long? _remaining = limit;

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

    /// <summary>Moves forward by <paramref name="count"/> bytes, or to the end, by reading: a server that ignored a range.</summary>
    /// <param name="count">How many bytes to skip.</param>
    /// <param name="cancellationToken">Cancels the skip.</param>
    public async Task SkipAsync(long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[(int)Math.Min(81920, Math.Max(1, count))];
        while (count > 0)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            count -= read;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await body.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            body.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }

    private int Allowed(int requested) => _remaining is { } remaining ? (int)Math.Min(requested, Math.Max(0, remaining)) : requested;
}
