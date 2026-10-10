namespace WriteableFileProviders;

internal static class StreamUtilities
{
    private const int SkipBufferSize = 81920;

    /// <summary>A new temp file, readable by its owner only on Unix, deleted when it is disposed.</summary>
    public static FileStream CreateTempFile()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = SkipBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), $"wfp-{Guid.NewGuid():N}.tmp"), options);
    }

    /// <summary>Moves a stream forward by <paramref name="count"/> bytes, or to its end: by seeking when it can, else by reading.</summary>
    public static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
            return;
        if (stream.CanSeek)
        {
            stream.Position = Math.Min(stream.Position + count, stream.Length);
            return;
        }
        var buffer = new byte[(int)Math.Min(SkipBufferSize, count)];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            count -= read;
        }
    }
}
