namespace WriteableFileProviders.Physical;

/// <summary>The options of <see cref="WriteablePhysicalFileProvider"/>: the folder on the local disk it is confined to.</summary>
/// <remarks>
/// <para>
/// Set the root through <see cref="RootDirectoryPath"/>, as an absolute path (<c>/srv/files</c>, <c>C:\files</c>,
/// <c>\\server\share\files</c>) or a path relative to the current directory, with <c>/</c> or <c>\</c> as the separator.
/// The provider resolves a relative path once, when it is built, and writes the absolute path back here.
/// </para>
/// <para>
/// <see cref="FileProviderOptions.RootDirectorySegments"/> holds the same path in the form 2026 stored it, which every
/// stored configuration has: a drive (<c>C:</c>), <c>\</c> for a network share, or <c>root:</c> for a path that starts at
/// the file system root, followed by the folder names (<c>/srv/files</c> is <c>root:</c>, <c>srv</c>, <c>files</c>).
/// Either property alone is enough, and both read back as stored before 2027.
/// </para>
/// </remarks>
public class PhysicalFileProviderOptions : FileProviderOptions
{
    private const string FileSystemRoot = "root:";
    private const string NetworkShare = "\\";

    /// <summary>
    /// The root folder as a path on this machine, such as <c>/srv/files</c> or <c>C:\files</c>; empty when unset. Setting
    /// it also takes the <c>root:/srv/files</c> form that 2026 wrote for an absolute path on Linux and macOS.
    /// </summary>
    public override string? RootDirectoryPath
    {
        get => ToPath(RootDirectorySegments);
        set => RootDirectorySegments = ToSegments(value);
    }

    // The segments as 2026 parsed them: a leading "\\" is a network share, a leading "/" the file system root.
    private static string[] ToSegments(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return [];
        string[] parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return [NetworkShare, .. parts];
        return path.StartsWith('/') ? [FileSystemRoot, .. parts] : parts;
    }

    // The real path the segments stand for, with this system's separator; 2026 left the "root:" marker in it (F79).
    private static string ToPath(string[] segments)
    {
        var separator = Path.DirectorySeparatorChar.ToString();
        var path = segments is [FileSystemRoot, .. var rest]
            ? separator + string.Join(separator, rest)
            : string.Join(separator, segments);
        // A drive alone, "C:", is the drive's current folder; the drive's root is "C:\".
        return path is [_, ':'] ? path + separator : path;
    }
}
