using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WriteableFileProviders.Physical
{
    /// <summary>Root containment is path based: symbolic links and junctions inside the root are followed.</summary>
    public class WriteablePhysicalFileProvider: WriteableFileProvider<PhysicalFileProviderOptions>
    {
        private PhysicalFileProviderOptions O { get; }
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        public WriteablePhysicalFileProvider(string rootPath): this(new PhysicalFileProviderOptions(){ RootDirectoryPath = rootPath})
        {

        }

        public WriteablePhysicalFileProvider(PhysicalFileProviderOptions options) : base(options)
        {
            O = options;
            if (string.IsNullOrEmpty(options.RootDirectoryPath))
                throw new ArgumentException("RootPath cannot be empty", nameof(options.RootDirectoryPath));

            var rootPrefix = "root:";
            if (options.RootDirectoryPath.StartsWith(rootPrefix) || !Path.IsPathRooted(options.RootDirectoryPath))
                options.RootDirectoryPath = Path.GetFullPath(options.RootDirectoryPath.Replace(rootPrefix, ""));
        }

        private WriteableFileInfo info(bool exists, long length, string physicalPath, DateTimeOffset lastModified, bool isDirectory, string[] relativeSegments) =>
            new WriteableFileInfo(this, new ProviderPath(relativeSegments), exists, isDirectory, length, lastModified, physicalPath);

        protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken)
        {
            var absolutePathSegments = resolved.AbsoluteSegments;
            var f = new System.IO.FileInfo(path(absolutePathSegments));
            return Task.FromResult<IWriteableFileInfo>(info(f.Exists, f.Exists ? f.Length : -1, f.FullName, f.Exists ? f.LastWriteTimeUtc : DefaultDateTime, false, GetRelativeSegments(absolutePathSegments)));
        }

        protected override Task<bool> FolderExistsCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken) =>
            Task.FromResult(Directory.Exists(path(resolved.AbsoluteSegments)));

        protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath resolved, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var absolutePathSegments = resolved.AbsoluteSegments;
            var dir = new DirectoryInfo(path(absolutePathSegments));
            if (!dir.Exists)
                yield break;
            foreach (var d in dir.GetDirectories())
                yield return info(true, -1, d.FullName, d.LastWriteTimeUtc, true, GetRelativeSegments(absolutePathSegments.Concat(new[] { d.Name }).ToArray()));
            foreach (var f in dir.GetFiles())
                yield return info(true, f.Length, f.FullName, f.LastWriteTimeUtc, false, GetRelativeSegments(absolutePathSegments.Concat(new[] { f.Name }).ToArray()));
            await Task.CompletedTask;
        }

        protected override Task<Stream> ReadFileCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken)
        {
            string p = path(resolved.AbsoluteSegments);
            if (!File.Exists(p))
                throw new FileNotFoundException("The file does not exist.", p);
            return Task.FromResult<Stream>(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        protected override async Task UpdateFileCoreAsync(ResolvedPath resolved, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
        {
            string p = path(resolved.AbsoluteSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            await using var fs = new FileStream(p, options.Mode == UpdateFileMode.Overwrite ? FileMode.Create: FileMode.Append, FileAccess.Write);
            await contents.CopyToAsync(fs, cancellationToken);
        }

        // NTFS and APFS match names without regard to case by default: a move from a.txt to A.txt is a rename.
        protected override bool IsCaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        protected override Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
        {
            var newPath = path(destination.AbsoluteSegments);
            var oldPath = path(source.AbsoluteSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            File.Move(oldPath, newPath);
            return Task.CompletedTask;
        }

        protected override Task<bool> DeleteCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken)
        {
            var p = path(resolved.AbsoluteSegments);
            if (isRoot(p))
                throw new InvalidOperationException("The provider root cannot be deleted or modified.");
            var ret = false;
            if (File.Exists(p))
            {
                File.Delete(p);
                ret = true;
            }
            else if (Directory.Exists(p))
            {
                Directory.Delete(p, true);
                ret = true;
            }
            return Task.FromResult(ret);
        }

        protected override bool IsSegmentAllowed(string part)
        {
            return part.IndexOf(':') < 0;
        }

        private string path(string[] p)
        {
            var full = fullPath(p);
            var root = fullPath(O.RootDirectorySegments);
            if (!string.Equals(full, root, PathComparison) &&
                !full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison))
                throw new ArgumentException("The path resolves outside the provider root.");
            return full;
        }

        private static string fullPath(string[] p)
        {
            var joined = string.Join(Path.DirectorySeparatorChar.ToString(), p).Replace("root:", "");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(joined + Path.DirectorySeparatorChar));
        }

        private bool isRoot(string p)
        {
            return string.Equals(p, fullPath(O.RootDirectorySegments), PathComparison);
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        internal static string[] getPathSegments(string p)
        {
            var ret = new List<string>();
            var val = p.Replace("/", @"\");
            if (p.StartsWith(@"\\"))
                ret.Add("\\");
            else if (p.StartsWith(@"/"))
                ret.Add(@"root:");
            ret.AddRange(val.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries));
            return ret.ToArray();
        }
    }
}
