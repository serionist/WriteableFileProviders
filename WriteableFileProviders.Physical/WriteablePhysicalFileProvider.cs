using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders.Physical
{
    public class WriteablePhysicalFileProvider: WriteableFileProvider<PhysicalFileProviderOptions>
    {
        private readonly PhysicalFileProvider _provider;
       

        public WriteablePhysicalFileProvider(string rootPath): this(new PhysicalFileProviderOptions(){ RootPath = rootPath})
        {
           
        }

        public WriteablePhysicalFileProvider(PhysicalFileProviderOptions options) : base(options)
        {
            if (string.IsNullOrEmpty(options.RootPath))
                throw new ArgumentException("RootPath cannot be empty", nameof(options.RootPath));
            _provider =  new PhysicalFileProvider(options.RootPath);
        }
            

        public override async Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            return;
        }

        public override async Task<IFileInfo> GetFileInfoAsync(params string[] pathSegments)
        {
            return _provider.GetFileInfo(getPath(pathSegments));
        }

        public override async Task<IDirectoryContents> GetDirectoryContentsAsync(params string[] pathSegments)
        {
            return _provider.GetDirectoryContents(getPath(pathSegments));
        }

        protected override async Task<IChangeToken> watchAsync(string filter)
        {
            return base.Watch(filter);
        }

        public override async Task<Stream> ReadFileAsync(params string[] pathSegments)
        {
            string p = getFullPath(pathSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            return GetFileInfo(getPath(pathSegments)).CreateReadStream();
        }

        public override async Task UpdateFileAsync(Stream contents, params string[] pathSegments)
        {
            string p = getFullPath(pathSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (var fs = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                contents.CopyTo(fs);
        }

        public override async Task<bool> DeleteAsync(params string[] pathSegments)
        {
            var p = getFullPath(pathSegments);
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

        public override async Task<LocalFileStream> CreateLocalFileStreamAsync(bool readOnly, params string[] pathSegments)
        {
            var p = getFullPath(pathSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            return new LocalFileStream(new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite), this, readOnly, pathSegments );
        }

        private static string getPath(string[] p)
        {
            return string.Join(Path.DirectorySeparatorChar.ToString(), p);
        }
        private string getFullPath(string[] path)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(_provider.Root, getPath(path)));
            }
            catch
            {
                return (string)null;
            }
            return fullPath;
        }
    }
}
