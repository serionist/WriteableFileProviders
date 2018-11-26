using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.File;

namespace WriteableFileProviders.AzureBlob
{
    public class WriteableAzureBlobFileContainer:WriteableFileProvider
    {
        private string _conString;
        private string _containerName;


        public string ConnectionString
        {
            get { return _conString; }
            set { _conString = value; }
        }

        public string ContainerName
        {
            get { return _containerName; }
            set { _containerName = value; }
        }

        public void asd()
        {
            new CloudStorageAccount()
            var storageAcc = CloudStorageAccount.Parse("");
            storageAcc
        }

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
