using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders.Physical
{
    public class WriteablePhysicalFileProvider: WriteableFileProvider
    {
        
        private PhysicalFileProvider _provider = null;

        public string RootPath
        {
            get => _provider?.Root;
            set => _provider = new PhysicalFileProvider(value);
        }

        public WriteablePhysicalFileProvider(string rootPath)
        {
            RootPath = rootPath;
            Directory.CreateDirectory(RootPath);
        }
        public WriteablePhysicalFileProvider():this(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))
        {
            
        }

        public override async Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            return;
        }

        protected override async Task<IFileInfo> getFileInfo(string normalizedPath, string name)
        {
            return _provider.GetFileInfo(getPath(normalizedPath));
        }

        protected override async Task<IDirectoryContents> getDirectoryContents(string normalizedPath, string name)
        {
            return _provider.GetDirectoryContents(getPath(normalizedPath));
        }

        protected override async Task<IChangeToken> watchAsync(string normalizedPath)
        {
            return base.Watch(getPath(normalizedPath));
        }

        protected override async Task<Stream> readFileAsync(string normalizedPath)
        {
            Directory.CreateDirectory(getFullPath(normalizedPath));
            return GetFileInfo(getPath(normalizedPath)).CreateReadStream();
        }

        protected override async Task writeFileAsync(string normalizedPath, Stream contents)
        {
            string p = getFullPath(normalizedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (var fs = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                contents.CopyTo(fs);
        }

        protected override async Task<bool> deleteAsync(string normalizedPath)
        {
            var p = getFullPath(normalizedPath);
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

        protected override async Task<LocalFileStream> createLocalFileStreamAsync(string normalizedPath)
        {
            var p = getFullPath(normalizedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            return new LocalFileStream(new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite), this, false, normalizedPath );
        }

        private static string getPath(string p)
        {
            return p.Replace('/', Path.DirectorySeparatorChar);
        }
        private string getFullPath(string path)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(RootPath, getPath(path)));
            }
            catch
            {
                return (string)null;
            }
            return fullPath;
        }
    }
}
