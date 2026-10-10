namespace WriteableFileProviders.SharePoint;

/// <summary>Where the provider's root sits in the document library (D7).</summary>
internal static class SharePointPaths
{
    private static readonly char[] _separators = ['/', '\\'];

    /// <summary>The work folder's segments: split on <c>/</c> and <c>\</c>, empty parts dropped.</summary>
    public static string[] WorkFolderSegments(string? workFolderPath) =>
        (workFolderPath ?? "").Split(_separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The library path of the provider root: the work folder followed by the root segments, after removing one leading
    /// copy of the work folder from the root segments (compared without regard to case). Caller paths are never stripped.
    /// Null when the root segments are a strict prefix of the work folder: the 2026 provider sent the caller paths that
    /// continued the work folder into the work folder itself, and every other path below a copy of the root, so no single
    /// root keeps both.
    /// </summary>
    public static string[]? RootSegments(string[] workFolder, string[] root)
    {
        if (workFolder.Length == 0)
            return root;
        if (StartsWith(root, workFolder))
            return [.. workFolder, .. root[workFolder.Length..]];
        if (root.Length > 0 && StartsWith(workFolder, root))
            return null;
        return [.. workFolder, .. root];
    }

    private static bool StartsWith(string[] segments, string[] prefix)
    {
        if (segments.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(segments[i], prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
