using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
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

       

    
        protected override async Task<IFileInfo> getFileInfo(params string[] pathSegments)
        {
            await Container.CreateIfNotExistsAsync();
            var file = Container.GetBlockBlobReference(getPath(pathSegments));
            var exists = await file.ExistsAsync();
            if (exists)
                await file.FetchAttributesAsync();
            return new FileInfo(this,  exists, file.Properties.Length,
                getPath(pathSegments), pathSegments.Last(), file.Properties.LastModified ?? DefaultDateTime, false);
            


        }

        protected override async Task<IDirectoryContents> getDirectoryContents(params string[] pathSegments)
        {
            var e = await EnumerateFiles(pathSegments, false);
            var ret = new List<IFileInfo>();
            var metadataJobs = new List<Task>();
            foreach (var file in e.files)
            {
                var t = new Task(async() =>
                {
                    await file.FetchAttributesAsync();
                });
                metadataJobs.Add(t);
                t.Start();
            }
            Task.WaitAll(metadataJobs.ToArray());

            foreach (var file in e.files)
                ret.Add(new FileInfo(this, true, file.Properties.Length, file.Name, getFileName(file.Name), file.Properties.LastModified ?? DefaultDateTime, false));
            foreach (var dir in e.directories)
                ret.Add(new FileInfo(this, true, -1, dir.Prefix, getFileName(dir.Prefix), DefaultDateTime, true));
          return new DirectoryContents(ret.Any(), ret.ToArray());
        }

        protected override Task<IChangeToken> watchAsync(string filter)
        {
            throw new NotImplementedException("Watch is not supported in WriteableAzureBlobFileProvider");
        }

        protected override async Task<Stream> readFileAsync(params string[] pathSegments)
        {
            var file = Container.GetBlockBlobReference(getPath(pathSegments));
            return await file.OpenReadAsync();
        }

        protected override async Task writeFileAsync(Stream contents, params string[] pathSegments)
        {
            var file = Container.GetBlockBlobReference(getPath(pathSegments));
            await file.UploadFromStreamAsync(contents);

        }

        protected override async Task<bool> deleteAsync(params string[] pathSegments)
        {
            var file = Container.GetBlockBlobReference(getPath(pathSegments));
            if (await file.DeleteIfExistsAsync())
                return true;
            var ret = false;
            foreach (var f in (await EnumerateFiles(pathSegments, true)).files)
                ret = await f.DeleteIfExistsAsync();
            return ret;
        }

        private async Task<(CloudBlockBlob[] files, CloudBlobDirectory[] directories)> EnumerateFiles(string[] pathSegments, bool deep)
        {
            var ret = new List<CloudBlockBlob>();
            var retDirs = new List<CloudBlobDirectory>();
            var contToken = new BlobContinuationToken();
            while (contToken != null)
            {
                var req = await Container.ListBlobsSegmentedAsync($"{getPath(pathSegments)}/", deep, BlobListingDetails.None, null,
                    contToken, new BlobRequestOptions(), new OperationContext());
                contToken = req.ContinuationToken;
                ret.AddRange(req.Results.OfType<CloudBlockBlob>());
                retDirs.AddRange(req.Results.OfType<CloudBlobDirectory>());
            }
            return (ret.ToArray(), retDirs.ToArray());

        }
       
        
        private string getPath(string[] pathSegments)
        {
            return string.Join("/", pathSegments);
        }

        private string getFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (!path.Contains("/")) return path;
            var ret = path;
            if (ret.EndsWith("/")) ret = ret.Substring(0, ret.Length - 1);
            return ret.Substring(ret.LastIndexOf("/") + 1);
        }
    }
}
