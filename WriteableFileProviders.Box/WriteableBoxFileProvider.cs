using Box.V2;
using Box.V2.Config;
using Box.V2.Exceptions;
using Box.V2.JWTAuth;
using Box.V2.Models;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WriteableFileProviders.Box
{
    public class WriteableBoxFileProvider : WriteableFileProvider<BoxFileProviderOptions>
    {
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        private readonly BoxJWTAuth jwtAuth;
        private string accessToken;
        private DateTime tokenExpirationTime;
        private BoxClient Client;
        private readonly string RootFolderId;
        private const long MAX_SIMPLE_UPLOAD_SIZE = 50 * 1024 * 1024; // 50 MB

        private readonly SemaphoreSlim tokenRefreshSemaphore = new SemaphoreSlim(1, 1);

        public WriteableBoxFileProvider(BoxFileProviderOptions options) : base(options)
        {
            if (string.IsNullOrEmpty(options.ClientId)) throw new ArgumentException("ClientId is required");
            if (string.IsNullOrEmpty(options.ClientSecret)) throw new ArgumentException("ClientSecret is required");
            if (string.IsNullOrEmpty(options.EnterpriseId)) throw new ArgumentException("EnterpriseId is required");
            if (string.IsNullOrEmpty(options.PublicKeyId)) throw new ArgumentException("PublicKeyId is required");
            if (string.IsNullOrEmpty(options.PrivateKey)) throw new ArgumentException("PrivateKey is required");

            var boxConfig = new BoxConfig(
                options.ClientId,
                options.ClientSecret,
                options.EnterpriseId,
                options.PublicKeyId,
                options.PrivateKey,
                options.Passphrase);

            RootFolderId = options.RootFolderId ?? "0";
            jwtAuth = new BoxJWTAuth(boxConfig);
            initializeOAuthSessionAsync().Wait();
            Client = jwtAuth.AdminClient(accessToken);
        }

        private async Task initializeOAuthSessionAsync()
        {
            accessToken = await jwtAuth.AdminTokenAsync();
            tokenExpirationTime = DateTime.UtcNow.AddSeconds(3600);
        }

        private async Task ensureValidSessionAsync()
        {
            if (string.IsNullOrEmpty(accessToken) || tokenExpirationTime <= DateTime.UtcNow.AddMinutes(5))
            {
                await tokenRefreshSemaphore.WaitAsync();
                try
                {
                    // Double-check inside the lock, because another thread might have refreshed the token already
                    if (string.IsNullOrEmpty(accessToken) || tokenExpirationTime <= DateTime.UtcNow.AddMinutes(5))
                    {
                        await initializeOAuthSessionAsync();
                        Client = jwtAuth.AdminClient(accessToken);
                    }
                }
                finally
                {
                    tokenRefreshSemaphore.Release();
                }
            }
        }

        private async Task<(BoxItem item, bool isFolder)> resolvePath(string[] pathSegments, bool expectFolder = false)
        {
            string parentId = RootFolderId;
            BoxItem current = null;

            foreach (var segment in pathSegments)
            {
                bool found = false;
                int offset = 0;
                const int limit = 500;

                while (!found)
                {
                    try
                    {
                        var items = await Client.FoldersManager.GetFolderItemsAsync(parentId, limit, offset);
                        foreach (var entry in items.Entries)
                        {
                            if (entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                            {
                                current = entry;
                                parentId = entry.Id;
                                found = true;
                                break;
                            }
                        }

                        if (found)
                            break;

                        offset += limit;
                        if (offset >= items.TotalCount)
                            break;
                    }
                    catch (BoxException ex)
                    {
                        throw new BoxException($"Box API error while resolving path segment '{segment}': {ex.Message}", ex);
                    }
                    catch (Exception ex)
                    {
                        throw new ApplicationException($"Error resolving path segment '{segment}'", ex);
                    }
                }
                if (!found)
                    return (null, false);
            }
            bool isFolder = current?.Type == "folder";
            if (expectFolder && !isFolder)
                return (null, false);
            return (current, isFolder);
        }

        protected override async Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            try
            {
                await ensureValidSessionAsync();
                var (file, isFolder) = await resolvePath(absolutePathSegments);
                if (file == null)
                    return new FileInfo(this, false, 0, string.Join("/", absolutePathSegments), absolutePathSegments.Last(), 
                        DefaultDateTime, false, getRelativeSegments(absolutePathSegments));
                return new FileInfo(this,
                    !isFolder,
                    isFolder ? -1 : (file as BoxFile)?.Size ?? 0,
                    string.Join("/", absolutePathSegments),
                    absolutePathSegments.Last(),
                    file.ModifiedAt?.DateTime ?? DefaultDateTime,
                    isFolder,
                    getRelativeSegments(absolutePathSegments));
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error retrieving file info for {string.Join("/", absolutePathSegments)}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Error retrieving file info for {string.Join("/", absolutePathSegments)}", ex);
            }
        }

        protected override async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            try
            {
                await ensureValidSessionAsync();
                var (folder, isFolder) = await resolvePath(absolutePathSegments, expectFolder: true);
                if (folder == null || !isFolder)
                    return new DirectoryContents(false, Array.Empty<IFileInfo>());

                var results = new List<IFileInfo>();
                int offset = 0;
                const int limit = 500;

                while (true)
                {
                    var items = await Client.FoldersManager.GetFolderItemsAsync(folder.Id, limit, offset);

                    foreach (var item in items.Entries)
                    {
                        results.Add(new FileInfo(this,
                            item.Type == "file",
                            item is BoxFile f ? f.Size ?? 0 : -1,
                            item.Name,
                            item.Name,
                            item.ModifiedAt?.DateTime ?? DefaultDateTime,
                            item.Type == "folder",
                            getRelativeSegments(absolutePathSegments.Concat(new[] { item.Name }).ToArray())));
                    }

                    offset += limit;
                    if (offset >= items.TotalCount)
                        break;
                }
                return new DirectoryContents(true, results.ToArray());
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error reading directory contents: {string.Join("/", absolutePathSegments)}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Error reading directory contents: {string.Join("/", absolutePathSegments)}", ex);
            }
        }

        protected override async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            try
            {
                await ensureValidSessionAsync();
                var (file, _) = await resolvePath(absolutePathSegments);
                if (file is not BoxFile boxFile)
                    throw new FileNotFoundException($"File not found: {string.Join("/", absolutePathSegments)}");
                return await Client.FilesManager.DownloadAsync(boxFile.Id);
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error reading file: {string.Join("/", absolutePathSegments)}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Failed to read file: {string.Join("/", absolutePathSegments)}", ex);
            }
        }

        protected override async Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
        {
            try
            {
                await ensureValidSessionAsync();
                var parentPath = absolutePathSegments[..^1];
                var fileName = absolutePathSegments.Last();
                var (folder, folderIsValid) = await resolvePath(parentPath, expectFolder: true);
                if (!folderIsValid)
                    throw new DirectoryNotFoundException($"Parent folder not found: {string.Join("/", parentPath)}");

                var (existing, isExistingFolder) = await resolvePath(absolutePathSegments);
                contents.Position = 0;
                long fileSize = contents.Length;

                if (fileSize <= MAX_SIMPLE_UPLOAD_SIZE)
                    await performSimpleUpload(contents, mode, fileName, folder, existing); // Simple upload for files 50 MB or smaller
                else
                    await performChunkedUpload(contents, mode, fileName, folder, existing); // Chunked upload for large files
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error updating file: {string.Join("/", absolutePathSegments)}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Failed to update file: {string.Join("/", absolutePathSegments)}", ex);
            }
        }

        private async Task performSimpleUpload(Stream contents, UpdateFileMode mode, string fileName, BoxItem folder, BoxItem existing)
        {
            if (mode == UpdateFileMode.Overwrite && existing is BoxFile existingFile)
                await Client.FilesManager.UploadNewVersionAsync(existingFile.Name, existingFile.Id, contents);
            else
                await Client.FilesManager.UploadAsync(new BoxFileRequest
                {
                    Name = fileName,
                    Parent = new BoxRequestEntity { Id = folder.Id }
                }, contents);
        }

        private async Task performChunkedUpload(Stream contents, UpdateFileMode mode, string fileName, BoxItem folder, BoxItem existing)
        {
            if (mode == UpdateFileMode.Overwrite && existing is BoxFile existingFile)
                await Client.FilesManager.UploadNewVersionUsingSessionAsync(
                    contents,
                    existingFile.Id,
                    fileName,
                    timeout: TimeSpan.FromMinutes(10)
                );
            else
                await Client.FilesManager.UploadUsingSessionAsync(
                    contents,
                    fileName,
                    folder.Id,
                    timeout: TimeSpan.FromMinutes(10)
                );
        }

        protected override async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            try
            {
                await ensureValidSessionAsync();
                var (item, isFolder) = await resolvePath(absolutePathSegments);
                if (item == null) return false;

                if (isFolder)
                    await Client.FoldersManager.DeleteAsync(item.Id, recursive: true);
                else
                    await Client.FilesManager.DeleteAsync(item.Id);

                return true;
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error deleting item: {string.Join("/", absolutePathSegments)}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Failed to delete item: {string.Join("/", absolutePathSegments)}", ex);
            }
        }

        protected override async Task move(string[] oldSegments, string[] newSegments)
        {
            try
            {
                await ensureValidSessionAsync();
                var (item, _) = await resolvePath(oldSegments);
                if (item == null)
                    throw new FileNotFoundException($"Source item not found: {string.Join("/", oldSegments)}");

                var (newParent, isFolder) = await resolvePath(newSegments[..^1], expectFolder: true);
                if (!isFolder)
                    throw new DirectoryNotFoundException($"Destination folder not found: {string.Join("/", newSegments[..^1])}");

                var newName = newSegments.Last();

                if (item is BoxFile file)
                {
                    await Client.FilesManager.UpdateInformationAsync(new BoxFileRequest
                    {
                        Id = file.Id,
                        Name = newName,
                        Parent = new BoxRequestEntity { Id = newParent.Id }
                    });
                }
                else if (item is BoxFolder folder)
                {
                    await Client.FoldersManager.UpdateInformationAsync(new BoxFolderRequest
                    {
                        Id = folder.Id,
                        Name = newName,
                        Parent = new BoxRequestEntity { Id = newParent.Id }
                    });
                }
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error moving item from {string.Join("/", oldSegments)} to {string.Join("/", newSegments)}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Failed to move item from {string.Join("/", oldSegments)} to {string.Join("/", newSegments)}", ex);
            }
        }

        protected override async Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            try
            {
                await ensureValidSessionAsync();
                var (item, isFolder) = await resolvePath(absolutePathSegments);
                if (item is not BoxFile file)
                    throw new FileNotFoundException($"File not found or is a folder: {string.Join("/", absolutePathSegments)}");

                // Retrieves the temporary direct Uri to a file (valid for 15 minutes)
                var downloadUri = await Client.FilesManager.GetDownloadUriAsync(file.Id);
                return downloadUri.ToString();
            }
            catch (BoxException ex)
            {
                throw new BoxException($"Box API error while retrieving download URL: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Unexpected error retrieving download URL: {string.Join("/", absolutePathSegments)}", ex);
            }
        }


        protected override Task<IChangeToken> watchAsync(string filter)
        {
            throw new PlatformNotSupportedException("Watch is not supported in WriteableBoxFileProvider");
        }
    }
}
