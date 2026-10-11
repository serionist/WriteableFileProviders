using Microsoft.Win32.SafeHandles;

namespace WriteableFileProviders.Physical;

/// <summary>
/// A forward-only read of <paramref name="length"/> bytes of an open file from <paramref name="start"/>, through
/// <see cref="RandomAccess"/>. It owns the handle: the bytes are those of the file the handle opened, even when another
/// file replaces it by name while it is read.
/// </summary>
/// <param name="handle">The file, opened for reading.</param>
/// <param name="start">The first byte.</param>
/// <param name="length">How many bytes, at most; the read also ends at the end of the file.</param>
internal sealed class FileRangeStream(SafeFileHandle handle, long start, long length) : Stream
{
    private readonly long _end = start + length;
    private long _position = start;

    public override bool CanRead => !handle.IsClosed;

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
        var read = RandomAccess.Read(handle, buffer[..Available(buffer.Length)], _position);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await RandomAccess.ReadAsync(handle, buffer[..Available(buffer.Length)], _position, cancellationToken).ConfigureAwait(false);
        _position += read;
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
            handle.Dispose();
        base.Dispose(disposing);
    }

    private int Available(int requested) => (int)Math.Min(requested, _end - _position);
}
