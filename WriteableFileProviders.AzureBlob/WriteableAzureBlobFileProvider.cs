using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.File;

namespace WriteableFileProviders.AzureBlob
{
    public class WriteableAzureBlobFileProvider:WriteableFileProvider<AzureFileProviderOptions>
    {
        private CloudBlobContainer Container { get;  }
        public WriteableAzureBlobFileProvider(AzureFileProviderOptions options) : base(options)
        {
            if (string.IsNullOrEmpty(Options.ConnectionString))
                throw new ArgumentException("ConnectionString is required", nameof(Options.ConnectionString));
            if (string.IsNullOrEmpty(Options.ContainerName))
                throw new ArgumentException("ContainerName is required", nameof(Options.ContainerName));
            var storageAcc = CloudStorageAccount.Parse(Options.ConnectionString);
            Container = storageAcc.CreateCloudBlobClient().GetContainerReference(Options.ContainerName);
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
