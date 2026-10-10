using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders.GoogleCloudStorage
{
    /// <summary>
    /// A writeable file provider backed by a Google Cloud Storage bucket.
    ///
    /// GCS is a flat object store (object keys may contain '/'). This provider presents the keys
    /// as a virtual folder hierarchy using '/' as the delimiter, mirroring the behaviour of the
    /// Amazon S3 and Azure Blob providers.
    /// </summary>
    public class WriteableGoogleCloudStorageFileProvider : WriteableFileProvider<GoogleCloudStorageFileProviderOptions>
    {

        private WriteableFileInfo legacyInfo(bool exists, long length, string physicalPath, string name, DateTimeOffset lastModified, bool isDirectory, string[] relativeSegments) =>
            new WriteableFileInfo(this, new ProviderPath(relativeSegments), exists, isDirectory, length, lastModified, physicalPath);

        protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken) =>
            getFileInfoAsync(resolved.AbsoluteSegments);

        protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath resolved, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in await getDirectoryContentsAsync(resolved.AbsoluteSegments))
                yield return (IWriteableFileInfo)entry;
        }

        protected override Task<Stream> ReadFileCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken) =>
            readFileAsync(resolved.AbsoluteSegments);

        protected override Task UpdateFileCoreAsync(ResolvedPath resolved, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) =>
            updateFileAsync(contents, options.Mode, resolved.AbsoluteSegments);

        protected override Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken) =>
            move(source.AbsoluteSegments, destination.AbsoluteSegments);

        protected override Task<bool> DeleteCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken) =>
            deleteAsync(resolved.AbsoluteSegments);

        protected override bool SupportsSignedUrl(SignedUrlAccess permission) => permission == SignedUrlAccess.Read;

        protected override async Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath resolved, SignedUrlOptions options, CancellationToken cancellationToken) =>
            new SignedUrl { Url = new Uri(await getDownloadUrl(resolved.AbsoluteSegments, options.Validity)), ExpiresOn = DateTimeOffset.UtcNow.Add(options.Validity), Kind = SignedUrlKind.Presigned };
        private const string DefaultContentType = "application/octet-stream";
        private const int DeletePageSize = 1000;
        private const int DeleteParallelism = 8;
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        private StorageClient Client { get; }
        private UrlSigner UrlSigner { get; }

        public WriteableGoogleCloudStorageFileProvider(GoogleCloudStorageFileProviderOptions options) : base(options)
        {
            if (string.IsNullOrEmpty(Options.BucketName))
                throw new ArgumentException("BucketName is required", nameof(Options.BucketName));

            var credential = createCredential();
            Client = StorageClient.Create(credential);
            // Build the signer up front (this does no network I/O). A service account key signs
            // locally; a Workload Identity / impersonated identity signs via the IAM signBlob API
            // when GetDownloadUrlAsync is called. Only a credential that genuinely cannot sign
            // throws here - in that case leave it null and fail clearly at call time.
            try
            {
                UrlSigner = UrlSigner.FromCredential(credential);
            }
            catch (InvalidOperationException)
            {
                UrlSigner = null;
            }
        }

        private GoogleCredential createCredential()
        {
            // 1. Discrete service account fields (the UI-friendly path).
            if (!string.IsNullOrEmpty(Options.ClientEmail) && !string.IsNullOrEmpty(Options.PrivateKey))
            {
                var initializer = new ServiceAccountCredential.Initializer(Options.ClientEmail)
                {
                    ProjectId = Options.ProjectId
                }.FromPrivateKey(NormalizePem(Options.PrivateKey));
                return GoogleCredential.FromServiceAccountCredential(new ServiceAccountCredential(initializer));
            }
            // 2. Whole service account key JSON as one string (e.g. from a secret store).
            if (!string.IsNullOrEmpty(Options.CredentialsJson))
                return CredentialFactory.FromJson<ServiceAccountCredential>(Options.CredentialsJson).ToGoogleCredential();
            // 3. Application Default Credentials. On GKE/GCE this resolves via Workload Identity /
            // the metadata server, keeping secrets out of the app configuration.
            return GoogleCredential.GetApplicationDefault();
        }

        internal static new string NormalizePem(string pem) => WriteableFileProvider<GoogleCloudStorageFileProviderOptions>.NormalizePem(pem);

        private async Task<IWriteableFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            var key = getPath(absolutePathSegments);
            if (absolutePathSegments.Length == 0)
                return legacyInfo(false, -1, key, "", DefaultDateTime, false,
                    GetRelativeSegments(absolutePathSegments));
            try
            {
                var obj = await Client.GetObjectAsync(Options.BucketName, key);
                return legacyInfo(true, (long)(obj.Size ?? 0), key, getFileName(key),
                    obj.UpdatedDateTimeOffset ?? DefaultDateTime, false, GetRelativeSegments(absolutePathSegments));
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                return legacyInfo(false, -1, key, getFileName(key), DefaultDateTime, false,
                    GetRelativeSegments(absolutePathSegments));
            }
        }

        private async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var prefix = getDirPrefix(absolutePathSegments);
            var ret = new List<IFileInfo>();
            var options = new ListObjectsOptions { Delimiter = "/" };
            await foreach (var page in Client.ListObjectsAsync(Options.BucketName, prefix, options).AsRawResponses())
            {
                if (page.Items != null)
                    foreach (var obj in page.Items)
                    {
                        // Skip a directory placeholder object (a key equal to the prefix itself).
                        if (obj.Name == prefix) continue;
                        var name = getFileName(obj.Name);
                        if (string.IsNullOrEmpty(name)) continue;
                        ret.Add(legacyInfo(true, (long)(obj.Size ?? 0), obj.Name, name,
                            obj.UpdatedDateTimeOffset ?? DefaultDateTime, false,
                            GetRelativeSegments(absolutePathSegments.Concat(new[] { name }).ToArray())));
                    }
                if (page.Prefixes != null)
                    foreach (var dir in page.Prefixes)
                    {
                        var name = getFileName(dir);
                        if (string.IsNullOrEmpty(name)) continue;
                        ret.Add(legacyInfo(true, -1, dir.TrimEnd('/'), name, DefaultDateTime, true,
                            GetRelativeSegments(absolutePathSegments.Concat(new[] { name }).ToArray())));
                    }
            }
            return new DirectoryContents(ret.Any(), ret.ToArray());
        }


        private async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            // GCS only exposes object downloads into a caller-provided stream, so stream the
            // contents into a temp file (deleted on close) and hand that back. This keeps memory
            // usage flat even for very large objects.
            var tempFileName = Path.GetTempFileName();
            var tempStream = new FileStream(tempFileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096,
                FileOptions.DeleteOnClose);
            try
            {
                await Client.DownloadObjectAsync(Options.BucketName, getPath(absolutePathSegments), tempStream);
                tempStream.Position = 0;
                return tempStream;
            }
            catch
            {
                tempStream.Dispose();
                throw;
            }
        }

        private async Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
        {
            if (mode == UpdateFileMode.Append)
            {
                // GCS has no native append; download the existing object, append, and re-upload.
                // Disposing the local stream commits it back.
                await using var str = await CreateLocalFileStreamCoreAsync(Resolve(new ProviderPath(GetRelativeSegments(absolutePathSegments))), false, CancellationToken.None);
                str.Position = str.Length;
                await using (contents)
                    await contents.CopyToAsync(str);
            }
            else
            {
                await using (contents)
                    await Client.UploadObjectAsync(Options.BucketName, getPath(absolutePathSegments), DefaultContentType,
                        contents);
            }
        }

        private async Task move(string[] oldSegments, string[] newSegments)
        {
            await Client.CopyObjectAsync(Options.BucketName, getPath(oldSegments), Options.BucketName,
                getPath(newSegments));
            await deleteAsync(oldSegments);
        }

        private async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            if (absolutePathSegments.Length == 0 || absolutePathSegments.SequenceEqual(Options.RootDirectorySegments))
                throw new InvalidOperationException("The provider root cannot be deleted or modified.");
            var info = await getFileInfoAsync(absolutePathSegments);
            if (info.Exists)
            {
                await Client.DeleteObjectAsync(Options.BucketName, getPath(absolutePathSegments));
                return true;
            }

            // Not a single file - treat as a directory and delete everything under it, one page at a time.
            var prefix = getDirPrefix(absolutePathSegments);
            var deleted = false;
            var options = new ListObjectsOptions { PageSize = DeletePageSize };
            await foreach (var page in Client.ListObjectsAsync(Options.BucketName, prefix, options).AsRawResponses())
            {
                if (page.Items == null || page.Items.Count == 0)
                    continue;
                await Parallel.ForEachAsync(page.Items,
                    new ParallelOptions { MaxDegreeOfParallelism = DeleteParallelism },
                    async (obj, ct) =>
                    {
                        try
                        {
                            await Client.DeleteObjectAsync(Options.BucketName, obj.Name, cancellationToken: ct);
                        }
                        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                        {
                        }
                    });
                deleted = true;
            }
            return deleted;
        }

        private async Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            if (UrlSigner == null)
                throw new PlatformNotSupportedException(
                    "Signed download URLs require a credential that can sign: either an inline service " +
                    "account key (CredentialsJson) or a Workload Identity / impersonated identity that " +
                    "holds the iam.serviceAccounts.signBlob permission.");
            // Use the async signer. For non-service-account credentials (Workload Identity) signing
            // performs an IAM signBlob call; the synchronous Sign(...) overload can deadlock there.
            return await UrlSigner.SignAsync(Options.BucketName, getPath(absolutePathSegments), validity,
                HttpMethod.Get);
        }

        private string getPath(string[] absolutePathSegments)
        {
            return string.Join("/", absolutePathSegments);
        }

        private string getDirPrefix(string[] absolutePathSegments)
        {
            // The root directory lists with an empty prefix; otherwise list inside the folder.
            return absolutePathSegments.Length == 0 ? "" : getPath(absolutePathSegments) + "/";
        }

        private string getFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var ret = path;
            if (ret.EndsWith("/")) ret = ret.Substring(0, ret.Length - 1);
            if (ret.Contains("/"))
                ret = ret.Substring(ret.LastIndexOf("/") + 1);
            return ret;
        }
    }
}
