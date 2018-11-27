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

        protected override async Task<IFileInfo> getFileInfo(params string[] pathSegments)
        {
            return _provider.GetFileInfo(getPath(pathSegments));
        }

        protected override async Task<IDirectoryContents> getDirectoryContents(params string[] pathSegments)
        {
            return _provider.GetDirectoryContents(getPath(pathSegments));
        }

        protected override async Task<IChangeToken> watchAsync(string filter)
        {
            return base.Watch(filter);
        }

        protected override async Task<Stream> readFileAsync(params string[] pathSegments)
        {
            Directory.CreateDirectory(getFullPath(pathSegments));
            return GetFileInfo(getPath(pathSegments)).CreateReadStream();
        }

        protected override async Task writeFileAsync(Stream contents, params string[] pathSegments)
        {
            string p = getFullPath(pathSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (var fs = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                contents.CopyTo(fs);
        }

        protected override async Task<bool> deleteAsync(params string[] pathSegments)
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

        protected override async Task<LocalFileStream> createLocalFileStreamAsync(params string[] pathSegments)
        {
            var p = getFullPath(pathSegments);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            return new LocalFileStream(new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite), this, false, pathSegments );
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
