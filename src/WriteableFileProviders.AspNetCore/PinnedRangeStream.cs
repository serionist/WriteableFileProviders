using System.Buffers;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// One version of a file as a seekable read-only stream, for the gateway's downloads. The first read opens one ranged
/// read of the provider (<see cref="IWriteableFileProvider.ReadRangeAsync"/>) from the current position, pinned with
/// <see cref="ReadOptions.IfMatch"/> to <paramref name="etag"/>, and later reads keep reading it while they stay
/// contiguous; only a seek elsewhere, or a read past the span it opened, opens another. So a whole download, or one HTTP
/// range, is one request to the store. A file that changed throws <see cref="PreconditionFailedException"/> when a range
/// opens, and one that ends early throws <see cref="IOException"/>.
/// </summary>
/// <param name="provider">The provider.</param>
/// <param name="path">The file.</param>
/// <param name="length">The file's length, as read with <paramref name="etag"/>.</param>
/// <param name="etag">The version to pin.</param>
/// <param name="window">
/// The span a single HTTP range asks for: a read that starts inside it opens a range that ends where it ends, so the store
/// sends no more than is served. Null opens every range to the end of the file.
/// </param>
internal sealed class PinnedRangeStream(IWriteableFileProvider provider, ProviderPath path, long length, string etag, (long Start, long End)? window) : Stream
{
    private Stream? _source;
    private long _sourcePosition;
    private long _sourceEnd;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= length || buffer.IsEmpty)
            return 0;
        if (_source is null || _sourcePosition != _position || _position >= _sourceEnd)
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        var count = (int)Math.Min(buffer.Length, _sourceEnd - _position);
        var read = await _source!.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        if (read == 0)
            throw new IOException($"The file '{path}' ended at byte {_position}, before its length of {length}: it changed while it was read.");
        _position += read;
        _sourcePosition += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await CloseSourceAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source?.Dispose();
            _source = null;
        }
        base.Dispose(disposing);
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        await CloseSourceAsync().ConfigureAwait(false);
        var end = window is { } span && _position >= span.Start && _position <= span.End ? span.End + 1 : length;
        _source = await provider.ReadRangeAsync(path, _position, end - _position, new ReadOptions { IfMatch = etag }, cancellationToken).ConfigureAwait(false);
        _sourcePosition = _position;
        _sourceEnd = end;
    }

    private async Task CloseSourceAsync()
    {
        if (_source is { } source)
        {
            _source = null;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
