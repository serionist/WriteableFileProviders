using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders.Physical
{
    public class WriteablePhysicalFileProvider: WriteableFileProvider
    {
        public bool RootPath { get; set; }

        public override Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            throw new NotImplementedException();
        }

        protected override Task<IFileInfo> getFileInfo(string normalizedPath, string name)
        {
            throw new NotImplementedException();
        }

        protected override Task<IDirectoryContents> getDirectoryContents(string normalizedPath, string name)
        {
            throw new NotImplementedException();
        }

        protected override Task<IChangeToken> watchAsync(string normalizedPath)
        {
            throw new NotImplementedException();
        }

        protected override Task<Stream> readFileAsync(string normalizedPath)
        {
            throw new NotImplementedException();
        }

        protected override Task writeFileAsync(string normalizedPath, Stream contents)
        {
            throw new NotImplementedException();
        }

        protected override Task<bool> deleteAsync(string normalizedPath)
        {
            throw new NotImplementedException();
        }

        protected override Task<LocalFileStream> createLocalFileStreamAsync(string normalizedPath)
        {
            throw new NotImplementedException();
        }
    }
}
