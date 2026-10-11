namespace WriteableFileProviders;

/// <summary>
/// The stream a read member returns: the provider's own stream, unchanged in what it can do (seek, length, position),
/// whose failures reach the caller in the contract's types. An exception that escapes the provider's stream goes
/// through the map, which returns the exception to throw instead, or null to let it through as it is.
/// </summary>
/// <param name="inner">The provider's stream, owned from now on.</param>
/// <param name="map">The provider's error policy, with the token of the call that failed.</param>
internal sealed class ContractStream(Stream inner, Func<Exception, CancellationToken, Exception?> map) : Stream
{
    private bool _disposed;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanTimeout => inner.CanTimeout;

    public override long Length
    {
        get
        {
            try
            {
                return inner.Length;
            }
            catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
            {
                throw mapped;
            }
        }
    }

    public override long Position
    {
        get
        {
            try
            {
                return inner.Position;
            }
            catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
            {
                throw mapped;
            }
        }
        set
        {
            try
            {
                inner.Position = value;
            }
            catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
            {
                throw mapped;
            }
        }
    }

    public override int ReadTimeout
    {
        get => inner.ReadTimeout;
        set => inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => inner.WriteTimeout;
        set => inner.WriteTimeout = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
        {
            throw mapped;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (map(e, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (map(e, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        try
        {
            return inner.Seek(offset, origin);
        }
        catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
        {
            throw mapped;
        }
    }

    // A read stream is read-only, so writing to it fails in the provider's stream; these delegate rather than map.
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
            {
                throw mapped;
            }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();
            }
        }
        catch (Exception e) when (map(e, CancellationToken.None) is { } mapped)
        {
            throw mapped;
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}
