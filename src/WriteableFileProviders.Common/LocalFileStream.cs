namespace WriteableFileProviders;

/// <summary>
/// A local temp copy of a provider's file, from <see cref="IWriteableFileProvider.CreateLocalFileStreamAsync"/>. A
/// writeable stream uploads its content to the file when it is disposed, or when <see cref="CommitAsync"/> is called:
/// once, atomically, and again only after further writes. A dispose after an exception uploads too, so partially written
/// content is uploaded; set <see cref="SkipReUploadOnDispose"/> to prevent that. The temp file is deleted on dispose.
/// </summary>
public sealed class LocalFileStream : Stream
{
    private readonly Stream _inner;
    private readonly Func<Stream, CancellationToken, Task>? _commit;
    private bool _needsCommit;
    private bool _disposed;

    internal LocalFileStream(Stream inner, IWriteableFileProvider provider, ProviderPath path, bool readOnly, Func<Stream, CancellationToken, Task>? commit)
    {
        _inner = inner;
        Provider = provider;
        Path = path;
        ReadOnly = readOnly;
        _commit = commit;
        _needsCommit = !readOnly && commit is not null;
    }

    /// <summary>The provider the file belongs to.</summary>
    public IWriteableFileProvider Provider { get; }

    /// <summary>The file's path, relative to the provider root.</summary>
    public ProviderPath Path { get; }

    /// <summary>Whether the stream is read-only: it cannot be written, and never uploads.</summary>
    public bool ReadOnly { get; }

    /// <summary>The local temp file's full name, for the tests.</summary>
    internal string? TempFilePath => (_inner as FileStream)?.Name;

    /// <summary>When true, disposing the stream does not upload it.</summary>
    public bool SkipReUploadOnDispose { get; set; }

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed && _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !ReadOnly && _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>Uploads the content now. A later dispose uploads again only if the stream was written after this.</summary>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <exception cref="NotSupportedException">The stream is read-only.</exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReadOnly || _commit is null)
            throw new NotSupportedException("A read-only stream has nothing to upload.");
        await CommitCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWriteable();
        _inner.Write(buffer, offset, count);
        _needsCommit = true;
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWriteable();
        _inner.Write(buffer);
        _needsCommit = true;
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWriteable();
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _needsCommit = true;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        EnsureWriteable();
        _inner.SetLength(value);
        _needsCommit = true;
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <summary>Uploads the content unless read-only, skipped or already uploaded, then deletes the temp file.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            try
            {
                if (_needsCommit && !SkipReUploadOnDispose)
                    await CommitCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _disposed = true;
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            try
            {
                if (_needsCommit && !SkipReUploadOnDispose)
                    WriteableFileProviderExtensions.Wait(CommitCoreAsync(CancellationToken.None));
            }
            finally
            {
                _disposed = true;
                _inner.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private async Task CommitCoreAsync(CancellationToken cancellationToken)
    {
        // Upload at most once per change: a failed upload is not retried by a later dispose.
        _needsCommit = false;
        var position = _inner.Position;
        _inner.Position = 0;
        try
        {
            await _commit!(_inner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!_disposed && _inner.CanSeek)
                _inner.Position = position;
        }
    }

    private void EnsureWriteable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanWrite)
            throw new NotSupportedException("The stream is read-only.");
    }
}
