using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Internal;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

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
            

        public override async Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            return;
        }

        protected override async Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            var f = new System.IO.FileInfo(path(absolutePathSegments));
            return new FileInfo(this, f.Exists, f.Exists? f.Length:-1, f.FullName, f.Name, f.Exists? f.LastWriteTimeUtc:DefaultDateTime, false, getRelativeSegments(absolutePathSegments));
        }

        protected override async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var dir = new DirectoryInfo(path(absolutePathSegments));
            if (!dir.Exists)
                return new NotFoundDirectoryContents();
            var ret = new List<IFileInfo>();
            foreach (var d in dir.GetDirectories())
                ret.Add(new FileInfo(this, true, -1, d.FullName, d.Name, d.LastWriteTimeUtc, true, getRelativeSegments(absolutePathSegments.Concat(new[] { d.Name }).ToArray())));
            foreach (var f in dir.GetFiles())
                ret.Add(new FileInfo(this, true, f.Length, f.FullName, f.Name, f.LastWriteTimeUtc, false, getRelativeSegments(absolutePathSegments.Concat(new[] { f.Name }).ToArray())));
            return new DirectoryContents(true, ret.ToArray());

        }

        protected override async Task<IChangeToken> watchAsync(string filter)
        {
            throw new PlatformNotSupportedException("Watch is not supported in WriteablePhysicalFileProvider");
        }

        protected override async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            string p = path(absolutePathSegments);
            if (!File.Exists(p))
                throw new FileNotFoundException("The file does not exist.", p);
            return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        protected override async Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
        {
            string p = path(absolutePathSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            await using var fs = new FileStream(p, mode == UpdateFileMode.Overwrite ? FileMode.Create: FileMode.Append, FileAccess.Write);
            await contents.CopyToAsync(fs);
        }

        protected override Task move(string[] oldSegments, string[] newSegments)
        {
            var newPath = path(newSegments);
            var oldPath = path(oldSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            File.Move(oldPath, newPath);
            return Task.CompletedTask;
        }

        protected override async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            var p = path(absolutePathSegments);
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
            return ret;
        }

        protected override async Task<LocalFileStream> createLocalFileStreamAsync(bool readOnly, params string[] absolutePathSegments)
        {
            var p = path(absolutePathSegments);
            if (readOnly)
            {
                if (!File.Exists(p))
                    throw new FileNotFoundException("The file does not exist.", p);
                return new LocalFileStream(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), this, true, absolutePathSegments);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            return new LocalFileStream(new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                this, false, absolutePathSegments);
        }

        protected override bool isSegmentAllowed(string part)
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

        protected override Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            throw new NotSupportedException();
        }

       
    }
}
