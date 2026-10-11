namespace WriteableFileProviders;

internal static class StreamUtilities
{
    private const int TempBufferSize = 81920;

    /// <summary>A new temp file, readable by its owner only on Unix, deleted when it is disposed.</summary>
    public static FileStream CreateTempFile()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = TempBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), $"wfp-{Guid.NewGuid():N}.tmp"), options);
    }
}
