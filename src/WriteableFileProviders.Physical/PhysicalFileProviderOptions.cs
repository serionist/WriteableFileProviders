using System.Text.Json.Serialization;

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

    /// <summary>
    /// Whether to refuse a path that names, or goes through, a link inside the root: a symbolic link, a junction, or a
    /// Windows mount point (a volume mounted in a folder); an entry the system reports a link target for. False by
    /// default, which follows them, as 2026 did. When true, every member refuses such a path with
    /// <see cref="ArgumentException"/>, and listings leave links out. The root itself, and the folders above it, may
    /// still be links.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is not a link: other reparse points (cloud placeholders, deduplicated files, the layers of a Windows
    /// container) hold the entry's own content; hard links, and mounts on Linux and macOS (bind mounts included), are
    /// not visible as links. None of them is refused.
    /// </para>
    /// <para>
    /// The check looks at the path, one entry at a time from the root, just before each member acts, not atomically
    /// with it: a process that can create links inside the root can still swap one in between. A recursive delete of a
    /// folder checks the folder's own path, then goes into each subfolder by its path, so a link swapped in while it
    /// runs can steer it into a folder elsewhere and delete what is there. On Linux and macOS, an entry on the way that
    /// the process may not look up ends the check, as the member then reads the path as missing; on Windows, which can
    /// let a process through a folder it may not read, it throws <see cref="UnauthorizedAccessException"/>.
    /// <c>Watch</c> is unchanged: with the polling watcher, a change behind a linked folder can fire a token, which
    /// says only that something changed.
    /// </para>
    /// <para>
    /// Written to JSON only when set, so a configuration that does not use it keeps the 2026 shape. A reader older than
    /// 2027.1.0 builds the provider without it, and no error says so: going back to an older version, or deploying the
    /// configuration ahead of 2027.1.0, follows links again. New in 2027.1.0.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RefuseLinks { get; set; }

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

    // The real path the segments stand for, with this system's separator; 2026 left the "root:" marker in it on Linux
    // and macOS.
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
