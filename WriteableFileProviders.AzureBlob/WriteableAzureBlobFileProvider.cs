using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders.AzureBlob
{
    public class WriteableAzureBlobFileProvider:WriteableFileProvider<AzureFileProviderOptions>
    {
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        private BlobServiceClient ServiceClient { get;}
        private BlobContainerClient Container { get; }
        private StorageSharedKeyCredential Credential { get; }
        public WriteableAzureBlobFileProvider(AzureFileProviderOptions options) : base(options)
        {
            if (string.IsNullOrEmpty(Options.AccountName))
                throw new ArgumentException("AccountName is required", nameof(Options.AccountName));
            if (string.IsNullOrEmpty(Options.AccountKey))
                throw new ArgumentException("AccountKey is required", nameof(Options.AccountKey));
            if (string.IsNullOrEmpty(Options.ContainerName))
                throw new ArgumentException("ContainerName is required", nameof(Options.ContainerName));
            string blobEndpoint = string.Format("https://{0}.blob.core.windows.net", Options.AccountName);
            Credential = new StorageSharedKeyCredential(Options.AccountName, Options.AccountKey);
            ServiceClient = new BlobServiceClient(new Uri(blobEndpoint), Credential);
            Container = ServiceClient.GetBlobContainerClient(Options.ContainerName);
        }




        protected override async Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            try
            {
                var file = Container.GetBlobClient(getPath(absolutePathSegments));
                var properties = await file.GetPropertiesAsync();
                return new FileInfo(this, true, properties.Value.ContentLength, getPath(absolutePathSegments), absolutePathSegments.Last(), properties.Value.LastModified, false, getRelativeSegments(absolutePathSegments));
            }
            catch(RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.BlobNotFound)
                    return new FileInfo(this, false, 0, getPath(absolutePathSegments), absolutePathSegments.Last(), DefaultDateTime, false, getRelativeSegments(absolutePathSegments));
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    return await getFileInfoAsync(absolutePathSegments);
                }
                throw;
            }
        }

        protected override async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var e = await EnumerateFiles(absolutePathSegments, false);
            var ret = new List<IFileInfo>();
            foreach (var file in e.files)
                ret.Add(new FileInfo(this, true, file.Properties.ContentLength ?? 0, file.Name, getFileName(file.Name), file.Properties.LastModified ?? DefaultDateTime, false, getRelativeSegments(absolutePathSegments.Concat(new[] { file.Name }).ToArray())));
            foreach (var dir in e.directories)
                ret.Add(new FileInfo(this, false, -1, dir, getFileName(dir), DefaultDateTime, true, getRelativeSegments(absolutePathSegments.Concat(new[] { dir }).ToArray())));
          return new DirectoryContents(ret.Any(), ret.ToArray());
        }

        protected override Task<IChangeToken> watchAsync(string filter)
        {
            throw new NotImplementedException("Watch is not supported in WriteableAzureBlobFileProvider");
        }

        protected override async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            try
            {
                var file = Container.GetBlobClient(getPath(absolutePathSegments));
                var download = await file.DownloadAsync();
                return new AzureFileStream(download.Value.Content, download.Value.ContentLength);
            }
            catch (RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    return await readFileAsync(absolutePathSegments);
                }
                throw;
            }
        }

        protected override async Task updateFileAsync(Stream contents, params string[] absolutePathSegments)
        {
            // await Container.CreateIfNotExistsAsync();
            var file = Container.GetBlobClient(getPath(absolutePathSegments));
            await file.UploadAsync(contents, true);
        }

        protected override async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            try
            {
                var file = Container.GetBlobClient(getPath(absolutePathSegments));
                if (await file.DeleteIfExistsAsync())
                    return true;
                var ret = false;
                foreach (var f in (await EnumerateFiles(absolutePathSegments, true)).files)
                    ret = await Container.GetBlobClient(f.Name).DeleteIfExistsAsync();
                return ret;
            }
            catch (RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    return await deleteAsync(absolutePathSegments);
                }
                throw;
            }
           
        }

        private async Task<(BlobItem[] files, string[] directories)> EnumerateFiles(string[] absolutePathSegments, bool deep)
        {
            try
            {
                var files = new List<BlobItem>();
                var dirPath = $"{getPath(absolutePathSegments)}/";
                var items = Container.GetBlobsAsync(prefix: dirPath);
                await foreach (var i in items)
                    files.Add(i);
                return (files.Where(f =>
                {
                    var relativePath = f.Name.Substring(dirPath.Length);
                    return deep || !relativePath.Contains("/");
                }).ToArray(), files.Select(e => e.Name).Select(e => e.Substring(0, e.LastIndexOf("/") + 1)).Where(e => e.EndsWith("/")).Where(d =>
                {
                    var relativePath = d.Substring(dirPath.Length);
                    if (string.IsNullOrEmpty(relativePath))
                        return false;
                    relativePath = relativePath.Substring(0, relativePath.Length - 1);
                    return deep || !relativePath.Contains("/");
                }).Distinct().ToArray());
            }
            catch (RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    return await EnumerateFiles(absolutePathSegments, deep);
                }
                throw;
            }
            


        }
        protected override async Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            try
            {
                var file = Container.GetBlobClient(getPath(absolutePathSegments));
                var builder = new BlobSasBuilder()
                {
                    BlobContainerName = Container.Name,
                    BlobName = getPath(absolutePathSegments),
                    StartsOn = DateTimeOffset.UtcNow,
                    ExpiresOn = DateTimeOffset.UtcNow.Add(validity)
                };
                builder.SetPermissions(BlobAccountSasPermissions.Read);
                return new BlobUriBuilder(file.Uri)
                {
                    Sas = builder.ToSasQueryParameters(Credential)
                }.ToUri().ToString();
            }
            catch (RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    return await getDownloadUrl(absolutePathSegments, validity);
                }
                throw;
            }

            
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
        
    }
}
