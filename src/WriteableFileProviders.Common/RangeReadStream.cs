namespace WriteableFileProviders;

/// <summary>
/// A read-only stream that can seek, over a file read in ranges: it knows its length, moves its position freely, and
/// fetches at least one buffer of bytes from the current position whenever a read needs bytes it does not hold.
/// </summary>
/// <remarks>
/// Each fetch calls the range reader, which a provider pins to the version it opened, so a file that changes while it is
/// read throws <see cref="PreconditionFailedException"/> (an <see cref="IOException"/>). A fetch that returns fewer bytes
/// than the length promised throws <see cref="IOException"/> too.
/// </remarks>
internal sealed class RangeReadStream : Stream
{
    private readonly Func<long, long, CancellationToken, Task<Stream>> _readRange;
    private readonly int _bufferSize;
    private byte[]? _buffer;
    private long _bufferStart;
    private int _bufferCount;
    private long _position;
    private bool _disposed;

    /// <summary>Creates the stream.</summary>
    /// <param name="length">The file's length in bytes.</param>
    /// <param name="readRange">Reads <c>count</c> bytes from <c>offset</c>: called with an offset inside the file.</param>
    /// <param name="bufferSize">The least number of bytes each fetch reads.</param>
    public RangeReadStream(long length, Func<long, long, CancellationToken, Task<Stream>> readRange, int bufferSize = 1 << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        Length = length;
        _readRange = readRange ?? throw new ArgumentNullException(nameof(readRange));
        _bufferSize = bufferSize;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length { get; }

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= Length || buffer.IsEmpty)
            return 0;
        if (!Holds(_position))
            WriteableFileProviderExtensions.Wait(FillAsync(_position, CancellationToken.None));
        return CopyFromBuffer(buffer);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= Length || buffer.IsEmpty)
            return 0;
        if (!Holds(_position))
            await FillAsync(_position, cancellationToken).ConfigureAwait(false);
        return CopyFromBuffer(buffer.Span);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
        };
        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <summary>Not supported: the stream is read-only.</summary>
    /// <param name="value">Ignored.</param>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Not supported: the stream is read-only.</summary>
    /// <param name="buffer">Ignored.</param>
    /// <param name="offset">Ignored.</param>
    /// <param name="count">Ignored.</param>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _buffer = null;
        base.Dispose(disposing);
    }

    private bool Holds(long position) => _buffer is not null && position >= _bufferStart && position < _bufferStart + _bufferCount;

    private int CopyFromBuffer(Span<byte> destination)
    {
        var start = (int)(_position - _bufferStart);
        var count = Math.Min(destination.Length, _bufferCount - start);
        _buffer.AsSpan(start, count).CopyTo(destination);
        _position += count;
        return count;
    }

    private async Task FillAsync(long start, CancellationToken cancellationToken)
    {
        var count = (int)Math.Min(_bufferSize, Length - start);
        _buffer ??= new byte[(int)Math.Min(_bufferSize, Math.Max(1, Length))];
        // The fetch writes into the buffer: until it succeeds, the buffer holds nothing, so a failed or cancelled fetch
        // never leaves bytes of one range described as another.
        _bufferCount = 0;
        var range = await _readRange(start, count, cancellationToken).ConfigureAwait(false);
        int read;
        await using (range.ConfigureAwait(false))
            read = await range.ReadAtLeastAsync(_buffer.AsMemory(0, count), count, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read < count)
        {
            _buffer = null;
            throw new IOException($"The file ended at byte {start + read}, before its length of {Length}: it changed while it was read.");
        }
        _bufferStart = start;
        _bufferCount = read;
    }
}
