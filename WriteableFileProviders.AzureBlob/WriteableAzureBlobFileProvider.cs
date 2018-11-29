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




        protected override async Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            await Container.CreateIfNotExistsAsync();
            var file = Container.GetBlockBlobReference(getPath(absolutePathSegments));
            var exists = await file.ExistsAsync();
            if (exists)
                await file.FetchAttributesAsync();
            return new FileInfo(this,  exists, file.Properties.Length,
                getPath(absolutePathSegments), absolutePathSegments.Last(), file.Properties.LastModified ?? DefaultDateTime, false, getRelativeSegments(absolutePathSegments));
            


        }

        protected override async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var e = await EnumerateFiles(absolutePathSegments, false);
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
                ret.Add(new FileInfo(this, true, file.Properties.Length, file.Name, getFileName(file.Name), file.Properties.LastModified ?? DefaultDateTime, false, getRelativeSegments(absolutePathSegments.Concat(new []{file.Name}).ToArray())));
            foreach (var dir in e.directories)
                ret.Add(new FileInfo(this, true, -1, dir.Prefix, getFileName(dir.Prefix), DefaultDateTime, true, getRelativeSegments(absolutePathSegments.Concat(new[] { getFileName(dir.Prefix) }).ToArray())));
          return new DirectoryContents(ret.Any(), ret.ToArray());
        }

        protected override Task<IChangeToken> watchAsync(string filter)
        {
            throw new NotImplementedException("Watch is not supported in WriteableAzureBlobFileProvider");
        }

        protected override async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            var file = Container.GetBlockBlobReference(getPath(absolutePathSegments));
            return await file.OpenReadAsync();
        }

        protected override async Task updateFileAsync(Stream contents, params string[] absolutePathSegments)
        {
            var file = Container.GetBlockBlobReference(getPath(absolutePathSegments));
            await file.UploadFromStreamAsync(contents);

        }

        protected override async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            var file = Container.GetBlockBlobReference(getPath(absolutePathSegments));
            if (await file.DeleteIfExistsAsync())
                return true;
            var ret = false;
            foreach (var f in (await EnumerateFiles(absolutePathSegments, true)).files)
                ret = await f.DeleteIfExistsAsync();
            return ret;
        }

        private async Task<(CloudBlockBlob[] files, CloudBlobDirectory[] directories)> EnumerateFiles(string[] absolutePathSegments, bool deep)
        {
            var ret = new List<CloudBlockBlob>();
            var retDirs = new List<CloudBlobDirectory>();
            var contToken = new BlobContinuationToken();
            while (contToken != null)
            {
                var req = await Container.ListBlobsSegmentedAsync($"{getPath(absolutePathSegments)}/", deep, BlobListingDetails.None, null,
                    contToken, new BlobRequestOptions(), new OperationContext());
                contToken = req.ContinuationToken;
                ret.AddRange(req.Results.OfType<CloudBlockBlob>());
                retDirs.AddRange(req.Results.OfType<CloudBlobDirectory>());
            }
            return (ret.ToArray(), retDirs.ToArray());

        }
       
        
        private string getPath(string[] absolutePathSegments)
        {
            return string.Join("/", absolutePathSegments);
        }

        private string getFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (!path.Contains("/")) return path;
            var ret = path;
            if (ret.EndsWith("/")) ret = ret.Substring(0, ret.Length - 1);
            return ret.Substring(ret.LastIndexOf("/") + 1);
        }
        private string[] getRelativeSegments(string[] segments)
        {
            return segments.Skip(Options.RootDirectorySegments.Length).ToArray();
        }
    }
}
