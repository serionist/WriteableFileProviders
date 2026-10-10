using System;
using System.Linq;

namespace WriteableFileProviders.SharePoint
{
    internal static class SharePointPaths
    {
        internal static string NormalizeWorkFolderPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path.Replace('\\', '/').Trim('/');
        }

        // Paths whose leading segments equal the work folder (case-insensitive) are made relative to it;
        // any other path is used as-is, relative to the work folder.
        internal static string[] StripWorkFolder(string workFolderPath, string[] absolutePathSegments)
        {
            if (string.IsNullOrEmpty(workFolderPath))
                return absolutePathSegments;

            var workFolderSegments = NormalizeWorkFolderPath(workFolderPath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (absolutePathSegments.Length < workFolderSegments.Length)
                return absolutePathSegments;

            for (int i = 0; i < workFolderSegments.Length; i++)
            {
                if (!absolutePathSegments[i].Equals(workFolderSegments[i], StringComparison.OrdinalIgnoreCase))
                    return absolutePathSegments;
            }
            return absolutePathSegments.Skip(workFolderSegments.Length).ToArray();
        }

        internal static string GetRelativePath(string workFolderPath, string[] absolutePathSegments)
        {
            return string.Join("/", StripWorkFolder(workFolderPath, absolutePathSegments));
        }

        // True when the target is the work folder (or drive root) itself, or the provider root directory.
        internal static bool IsProviderRoot(string workFolderPath, string[] rootDirectorySegments, string[] absolutePathSegments)
        {
            return absolutePathSegments.Length <= (rootDirectorySegments?.Length ?? 0)
                || string.IsNullOrEmpty(GetRelativePath(workFolderPath, absolutePathSegments));
        }
    }
}
