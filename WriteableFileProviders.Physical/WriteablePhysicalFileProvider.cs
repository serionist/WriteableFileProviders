using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders.Physical
{
    public class WriteablePhysicalFileProvider: PhysicalFileProvider, IWriteableFileProvider
    {
        public WriteablePhysicalFileProvider(string RootDirectory) : base(RootDirectory)
        {
        }

        public WriteablePhysicalFileProvider(string RootDirectory, ExclusionFilters filters) : base(RootDirectory, filters)
        {
        }



        private string GetFullPath(string path)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(this.Root, path));
            }
            catch
            {
                return (string)null;
            }
            return fullPath;
        }

        public async Task<IFileInfo> GetFileInfoAsync(string subpath)
        {
            return GetFileInfo(subpath);
        }

        public async Task<IDirectoryContents> GetDirectoryContentsAsync(string subpath)
        {
            return GetDirectoryContents(subpath);
        }

        public async Task<IChangeToken> WatchAsync(string filter)
        {
            return Watch(filter);
        }


        public async Task<Stream> ReadFileAsync(string subpath)
        {
            return ReadFile(subpath);
        }

        public Stream ReadFile(string subpath)
        {
            return new FileStream(this.GetFullPath(subpath), FileMode.Open, FileAccess.Read);
        }

        public async Task UpdateFileAsync(string subpath, Stream contents)
        {
            UpdateFile(subpath, contents);
        }

        public void UpdateFile(string subpath, Stream contents)
        {
            contents.Position = 0;
            using (var fs = new FileStream(this.GetFullPath(subpath), FileMode.OpenOrCreate, FileAccess.ReadWrite))
                contents.CopyTo(fs);
        }

        public bool Delete(string subpath)
        {
            var p = this.GetFullPath(subpath);
            var ret = false;
            if (Directory.Exists(p))
            {
                ret = true;
                Directory.Delete(p);
            }
            else if (File.Exists(p))
            {
                ret = true;
                File.Delete(p);
            }
            return ret;
        }

        public async Task<bool> DeleteAsync(string subpath)
        {
            return Delete(subpath);
        }

        public Task<LocalFileStream> CreateLocalFileStreamAsync(string subpath)
        {
            throw new NotImplementedException();
        }

        public LocalFileStream CreateLocalFileStream(string subpath)
        {
            throw new NotImplementedException();
        }

        public Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            throw new NotImplementedException();
        }
        
    }
}
