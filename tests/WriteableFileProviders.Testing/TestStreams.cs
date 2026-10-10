using System.Text;

namespace WriteableFileProviders.Testing;

/// <summary>Streams for testing writes.</summary>
public static class TestStreams
{
    /// <summary>A stream of the UTF-8 text.</summary>
    /// <param name="text">The text.</param>
    public static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A deterministic pseudo-random content of the given size.</summary>
    /// <param name="size">The size in bytes.</param>
    /// <param name="seed">The seed.</param>
    public static byte[] Bytes(long size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}

/// <summary>A stream that yields some bytes, then throws: a source that fails in the middle of a write.</summary>
/// <param name="data">The bytes it yields before failing.</param>
public sealed class FailingStream(byte[] data) : Stream
{
    private int _position;

    /// <summary>Whether the stream was disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => !IsDisposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_position >= data.Length)
            throw new IOException("The source failed in the middle of the write.");
        var n = Math.Min(count, data.Length - _position);
        Array.Copy(data, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>A stream that runs an action the first time it is read: a change racing a write.</summary>
/// <param name="data">The bytes it yields.</param>
/// <param name="onFirstRead">What to do before the first read.</param>
public sealed class InterceptingStream(byte[] data, Action onFirstRead) : MemoryStream(data, writable: false)
{
    private bool _started;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        Start();
        return base.Read(buffer, offset, count);
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        Start();
        return base.Read(buffer);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Start();
        return base.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Start();
        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    private void Start()
    {
        if (_started)
            return;
        _started = true;
        onFirstRead();
    }
}
