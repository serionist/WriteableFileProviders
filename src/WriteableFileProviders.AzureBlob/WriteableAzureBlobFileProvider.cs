using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders.AzureBlob
{
    /// <summary>
    /// A writeable file provider backed by an Azure Blob Storage container. Files are stored as append blobs.
    /// <para>
    /// Writes are not atomic: an overwrite replaces the blob with an empty append blob and then appends the
    /// new content, so concurrent readers can observe partial content, and a failed upload leaves a partial blob.
    /// </para>
    /// <para>
    /// An existing blob of another type (for example a BlockBlob uploaded by another tool) is replaced by an
    /// append blob on overwrite; appending to it fails with a <see cref="RequestFailedException"/> (InvalidBlobType).
    /// </para>
    /// <para>
    /// The container is created on the first write; reads, listings and deletes never create it.
    /// </para>
    /// </summary>
    public class WriteableAzureBlobFileProvider : WriteableFileProvider<AzureFileProviderOptions>
    {
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MaxSasValidity = TimeSpan.FromDays(7);
        private const int DeletePageSize = 1000;
        private const int DeleteParallelism = 8;

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
            var protocol = string.IsNullOrEmpty(Options.Protocol) ? Uri.UriSchemeHttps : Options.Protocol.ToLowerInvariant();
            if (protocol != Uri.UriSchemeHttps && protocol != Uri.UriSchemeHttp)
                throw new ArgumentException("Protocol must be http or https", nameof(Options.Protocol));

            var sanitizedUrl = $"{Options.AccountName}.blob.core.windows.net";
            if (!string.IsNullOrEmpty(Options.Url))
                sanitizedUrl = Options.Url;
            var blobEndpoint = $"{protocol}://{sanitizedUrl}";
            Credential = new StorageSharedKeyCredential(Options.AccountName, Options.AccountKey);
            ServiceClient = new BlobServiceClient(new Uri(blobEndpoint), Credential);
            Container = ServiceClient.GetBlobContainerClient(Options.ContainerName);
        }

        protected override async Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            var path = getPath(absolutePathSegments);
            var name = absolutePathSegments.LastOrDefault() ?? "";
            if (absolutePathSegments.Length == 0)
                return new FileInfo(this, false, 0, path, name, DefaultDateTime, false, getRelativeSegments(absolutePathSegments));
            try
            {
                var file = Container.GetAppendBlobClient(path);
                var properties = await file.GetPropertiesAsync();
                return new FileInfo(this, true, properties.Value.ContentLength, path, name, properties.Value.LastModified, false, getRelativeSegments(absolutePathSegments));
            }
            catch(RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound || ex.ErrorCode == BlobErrorCode.ContainerNotFound)
            {
                return new FileInfo(this, false, 0, path, name, DefaultDateTime, false, getRelativeSegments(absolutePathSegments));
            }
        }

        protected override async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var prefix = getDirPrefix(absolutePathSegments);
            var ret = new List<IFileInfo>();
            var found = false;
            try
            {
                await foreach (var item in Container.GetBlobsByHierarchyAsync(delimiter: "/", prefix: prefix))
                {
                    found = true;
                    if (item.IsPrefix)
                    {
                        var name = item.Prefix.Substring(prefix.Length);
                        if (name.EndsWith("/"))
                            name = name.Substring(0, name.Length - 1);
                        if (name.Length == 0 || name.Contains("/"))
                            continue;
                        ret.Add(new FileInfo(this, true, -1, item.Prefix, name, DefaultDateTime, true,
                            getRelativeSegments(absolutePathSegments.Concat(new[] { name }).ToArray())));
                    }
                    else if (item.IsBlob)
                    {
                        var name = item.Blob.Name.Substring(prefix.Length);
                        if (name.Length == 0)
                            continue;
                        ret.Add(new FileInfo(this, true, item.Blob.Properties.ContentLength ?? 0, item.Blob.Name, name,
                            item.Blob.Properties.LastModified ?? DefaultDateTime, false,
                            getRelativeSegments(absolutePathSegments.Concat(new[] { name }).ToArray())));
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
            {
                return new DirectoryContents(false, new IFileInfo[0]);
            }
            return new DirectoryContents(found, ret.ToArray());
        }

        protected override Task<IChangeToken> watchAsync(string filter)
        {
            throw new PlatformNotSupportedException("Watch is not supported in WriteableAzureBlobFileProvider");
        }

        protected override async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            var file = Container.GetAppendBlobClient(getPath(absolutePathSegments));
            var download = await file.DownloadAsync();
            return new AzureFileStream(download.Value.Content, download.Value.ContentLength);
        }

        protected override async Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
        {
            var file = Container.GetAppendBlobClient(getPath(absolutePathSegments));
            Stream str;
            try
            {
                await file.CreateIfNotExistsAsync();
                str = await file.OpenWriteAsync(mode == UpdateFileMode.Overwrite);
            }
            catch (RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    await updateFileAsync(contents, mode, absolutePathSegments);
                    return;
                }
                throw;
            }

            await using (str)
                await contents.CopyToAsync(str);
        }

        protected override async Task move(string[] oldSegments, string[] newSegments)
        {
            var oldFile = Container.GetAppendBlobClient(getPath(oldSegments));
            var newFile = Container.GetAppendBlobClient(getPath(newSegments));
            var lease = oldFile.GetBlobLeaseClient();
            var leased = false;
            try
            {
                await lease.AcquireAsync(LeaseDuration);
                leased = true;
                await newFile.StartCopyFromUriAsync(oldFile.Uri);
                var deadline = DateTimeOffset.UtcNow + CopyTimeout;
                var renewAt = DateTimeOffset.UtcNow + LeaseRenewInterval;
                BlobProperties properties;
                while (true)
                {
                    properties = await newFile.GetPropertiesAsync();
                    if (properties.BlobCopyStatus != CopyStatus.Pending)
                        break;
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        try
                        {
                            await newFile.AbortCopyFromUriAsync(properties.CopyId);
                        }
                        catch (RequestFailedException) { }
                        throw new TimeoutException($"Copying the file did not complete within {CopyTimeout.TotalMinutes} minutes");
                    }
                    if (DateTimeOffset.UtcNow >= renewAt)
                    {
                        await lease.RenewAsync();
                        renewAt = DateTimeOffset.UtcNow + LeaseRenewInterval;
                    }
                    await Task.Delay(1000);
                }
                switch (properties.BlobCopyStatus)
                {
                    case CopyStatus.Success:
                        await oldFile.DeleteIfExistsAsync(conditions: new BlobRequestConditions { LeaseId = lease.LeaseId });
                        leased = false;
                        return;
                    case CopyStatus.Failed:
                    case CopyStatus.Aborted:
                        throw new ApplicationException($"Failed to copy file: {properties.CopyStatusDescription}");
                    default:
                        throw new ApplicationException($"Failed to copy file: Unknown status {properties.BlobCopyStatus}");
                }
            }
            catch (RequestFailedException ex)
            {
                if (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    await Container.CreateIfNotExistsAsync();
                    await move(oldSegments, newSegments);
                    return;
                }
                throw;
            }
            finally
            {
                if (leased)
                {
                    try
                    {
                        await lease.ReleaseAsync();
                    }
                    catch (Exception) { }
                }
            }
        }


        protected override async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            if (isProviderRoot(absolutePathSegments))
                throw new InvalidOperationException("The provider root cannot be deleted or modified.");
            try
            {
                var file = Container.GetAppendBlobClient(getPath(absolutePathSegments));
                if (await file.DeleteIfExistsAsync())
                    return true;
                var deleted = false;
                await foreach (var page in Container.GetBlobsAsync(prefix: getDirPrefix(absolutePathSegments)).AsPages(pageSizeHint: DeletePageSize))
                {
                    var blobs = page.Values;
                    var results = new bool[blobs.Count];
                    await Parallel.ForEachAsync(Enumerable.Range(0, blobs.Count),
                        new ParallelOptions { MaxDegreeOfParallelism = DeleteParallelism },
                        async (i, ct) => results[i] = await Container.GetBlobClient(blobs[i].Name).DeleteIfExistsAsync(cancellationToken: ct));
                    deleted |= results.Any(r => r);
                }
                return deleted;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
            {
                return false;
            }
        }

        protected override Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            if (validity > MaxSasValidity)
                validity = MaxSasValidity;
            var file = Container.GetBlobClient(getPath(absolutePathSegments));
            var builder = new BlobSasBuilder()
            {
                BlobContainerName = Container.Name,
                BlobName = getPath(absolutePathSegments),
                StartsOn = DateTimeOffset.UtcNow,
                ExpiresOn = DateTimeOffset.UtcNow.Add(validity)
            };
            if (file.Uri.Scheme == Uri.UriSchemeHttps)
                builder.Protocol = SasProtocol.Https;
            builder.SetPermissions(BlobAccountSasPermissions.Read);
            return Task.FromResult(new BlobUriBuilder(file.Uri)
            {
                Sas = builder.ToSasQueryParameters(Credential)
            }.ToUri().ToString());
        }


        private string getPath(string[] absolutePathSegments)
        {
            return string.Join("/", absolutePathSegments);
        }

        private string getDirPrefix(string[] absolutePathSegments)
        {
            return absolutePathSegments.Length == 0 ? "" : getPath(absolutePathSegments) + "/";
        }

        private bool isProviderRoot(string[] absolutePathSegments)
        {
            return absolutePathSegments.Length == 0 || absolutePathSegments.SequenceEqual(Options.RootDirectorySegments);
        }
    }
}
