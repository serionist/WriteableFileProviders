using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders.AmazonS3
{
    public class WriteableAmazonS3FileProvider : WriteableFileProvider<AmazonS3FileProviderOptions>
    {
        private const int MaxDeleteBatchSize = 1000;
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        AmazonS3Client Client { get; }
        public WriteableAmazonS3FileProvider(AmazonS3FileProviderOptions options) : base(options)
        {
            AWSCredentials credentials = Options.IsAnonymus
                ? (AWSCredentials) new AnonymousAWSCredentials()
                : new BasicAWSCredentials(Options.AccessKey, Options.SecretKey);
            Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(Options.BucketRegion));
        }

        private async Task<S3Object> firstObject(string prefix)
        {
            var response = await Client.ListObjectsV2Async(new ListObjectsV2Request()
            {
                BucketName = Options.BucketName,
                RequestPayer = RequestPayer.Requester,
                Prefix = prefix,
                MaxKeys = 1
            });
            return response.S3Objects.FirstOrDefault();
        }

        private WriteableFileInfo info(bool exists, long length, string physicalPath, DateTimeOffset lastModified, bool isDirectory, string[] relativeSegments) =>
            new WriteableFileInfo(this, new ProviderPath(relativeSegments), exists, isDirectory, length, lastModified, physicalPath);

        protected override async Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken)
        {
            var absolutePathSegments = resolved.AbsoluteSegments;
            var key = path(absolutePathSegments);
            var relativeSegments = GetRelativeSegments(absolutePathSegments);
            if (string.IsNullOrEmpty(key))
                return info(false, -1, key, DefaultDateTime, false, relativeSegments);

            //an exact key is always listed first under its own prefix
            var first = await firstObject(key);
            if (first != null && first.Key == key)
                return info(true, first.Size, key, first.LastModified, false, relativeSegments);

            var isDirectory = first != null
                              && (first.Key.StartsWith(key + "/", StringComparison.Ordinal) || await firstObject(key + "/") != null);
            return info(false, -1, key, DefaultDateTime, isDirectory, relativeSegments);
        }

        protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath resolved, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in await enumerateObjects(resolved.AbsoluteSegments, false))
                yield return (IWriteableFileInfo)entry;
        }

        /// <summary>
        /// Lists the objects under the given absolute segments: the provider root segments, then the path.
        /// </summary>
        /// <exception cref="ArgumentException">The segments do not start with the provider root, or a segment after it is not allowed.</exception>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use GetDirectoryContentsAsync for one level. This member is removed in 2027.0.0, which lists recursively through EnumerateAsync.")]
        public Task<List<IFileInfo>> EnumerateObjects(string[] absolutePathSegments, bool deep) =>
            enumerateObjects(checkAbsoluteSegments(absolutePathSegments, allowRoot: true), deep);

        // The public members that take absolute segments accept only the provider root followed by a valid path, so
        // they cannot reach keys outside the root.
        private string[] checkAbsoluteSegments(string[] absolutePathSegments, bool allowRoot)
        {
            var root = Options.RootDirectorySegments ?? new string[0];
            var segments = absolutePathSegments ?? new string[0];
            if (segments.Length < root.Length || !root.SequenceEqual(segments.Take(root.Length), StringComparer.Ordinal))
                throw new ArgumentException("The path segments must start with the provider root segments.", nameof(absolutePathSegments));
            var relative = ValidateSegments(segments.Skip(root.Length));
            if (!allowRoot && relative.Length == 0)
                throw new ArgumentException("The path must contain at least one segment.", nameof(absolutePathSegments));
            return root.Concat(relative).ToArray();
        }

        private async Task<List<IFileInfo>> enumerateObjects(string[] absolutePathSegments, bool deep)
        {
            var ret = new List<IFileInfo>();
            var dirnames = new List<string>();
            var seenDirnames = new HashSet<string>();
            ListObjectsV2Response results;
            var dirPath = this.dirPath(absolutePathSegments);
            string continuationToken = null;
            do
            {
                results = await Client.ListObjectsV2Async(new ListObjectsV2Request()
                {
                    BucketName = Options.BucketName,
                    Prefix = dirPath,
                    Delimiter = deep ? null : "/",
                    ContinuationToken = continuationToken,
                    RequestPayer = RequestPayer.Requester
                });
                continuationToken = results.NextContinuationToken;

                foreach (var r in results.S3Objects)
                {
                    var relativeParts = r.Key.Substring(dirPath.Length).Split('/');
                    if (relativeParts.Length > 1 && relativeParts[0].Length > 0 && seenDirnames.Add(relativeParts[0]))
                        dirnames.Add(relativeParts[0]);
                    //skip folder markers and keys containing empty segments
                    if (relativeParts.Any(string.IsNullOrEmpty)) continue;

                    ret.Add(info(true, r.Size, r.Key, r.LastModified, false, GetRelativeSegments(absolutePathSegments.Concat(relativeParts).ToArray())));
                }

                foreach (var p in results.CommonPrefixes)
                {
                    var d = p.Substring(dirPath.Length, p.Length - dirPath.Length - 1);
                    if (d.Length > 0 && seenDirnames.Add(d))
                        dirnames.Add(d);
                }
            } while (!string.IsNullOrEmpty(continuationToken));
            foreach (var d in dirnames)
                ret.Add(info(true, -1, dirPath + d, DefaultDateTime, true, GetRelativeSegments(absolutePathSegments.Concat(new[] { d }).ToArray())));

            return ret;
        }

        /// <summary>
        /// Reads a version of a file, given its absolute segments: the provider root segments, then the path.
        /// </summary>
        /// <remarks>
        /// On a variable typed <see cref="WriteableAmazonS3FileProvider"/>, a call such as <c>ReadFileAsync("dir", "file.txt")</c>
        /// binds to this member, not to the base overload. Use <see cref="ReadFileVersionAsync"/> for a version, and the base
        /// overloads (through <see cref="IWriteableFileProvider"/>) for the current file.
        /// </remarks>
        /// <exception cref="ArgumentException">The segments do not start with the provider root, or a segment after it is not allowed.</exception>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use ReadFileVersionAsync for a version, or ReadFileAsync through IWriteableFileProvider. This member is removed in 2027.0.0.")]
        public Task<Stream> ReadFileAsync(string versionId, params string[] absolutePathSegments) =>
            readObjectAsync(versionId, checkAbsoluteSegments(absolutePathSegments, allowRoot: false));

        /// <summary>Reads the file at the given path, relative to the provider root.</summary>
        public Task<Stream> ReadFileAsync(string subpath) => base.ReadFileAsync(subpath);

        /// <summary>Reads a version of the file at the given path segments, relative to the provider root.</summary>
        /// <param name="versionId">The S3 version id, or null or empty for the current version.</param>
        /// <param name="pathSegments">The path segments, relative to the provider root.</param>
        /// <exception cref="ArgumentException">A segment is not allowed, or the path is the provider root.</exception>
        public Task<Stream> ReadFileVersionAsync(string versionId, params string[] pathSegments)
        {
            var relative = ValidateSegments(pathSegments);
            if (relative.Length == 0)
                throw new ArgumentException("The path must contain at least one segment.", nameof(pathSegments));
            return readObjectAsync(versionId, (Options.RootDirectorySegments ?? new string[0]).Concat(relative).ToArray());
        }

        private async Task<Stream> readObjectAsync(string versionId, string[] absolutePathSegments)
        {
            if (string.IsNullOrEmpty(versionId))
                return (await Client.GetObjectAsync(Options.BucketName, path(absolutePathSegments))).ResponseStream;
            else
                return (await Client.GetObjectAsync(Options.BucketName, path(absolutePathSegments), versionId)).ResponseStream;
        }
        protected override Task<Stream> ReadFileCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken) => readObjectAsync(null, resolved.AbsoluteSegments);

        protected override async Task UpdateFileCoreAsync(ResolvedPath resolved, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
        {
            var absolutePathSegments = resolved.AbsoluteSegments;
            if (options.Mode == UpdateFileMode.Append)
            {
                await using var str = await CreateLocalFileStreamCoreAsync(resolved, false, cancellationToken);
                str.Position = str.Length;
                await using (contents)
                    await contents.CopyToAsync(str);
            }
            else
                await using (contents)
                    await Client.PutObjectAsync(new PutObjectRequest()
                    {
                        InputStream = contents,
                        BucketName = Options.BucketName,
                        AutoCloseStream = true,
                        AutoResetStreamPosition = false,
                        Key = path(absolutePathSegments),
                        RequestPayer = RequestPayer.Requester,
                        StorageClass = Options.StorageClassForNewFiles
                    });

        }

        /// <summary>
        /// Copies the object to the new key with a single CopyObject request, then deletes the original.
        /// S3 limits a single copy to objects of at most 5 GB, so larger objects cannot be moved.
        /// </summary>
        protected override Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken) =>
            move(source.AbsoluteSegments, destination.AbsoluteSegments);

        private async Task move(string[] oldSegments, string[] newSegments)
        {
            var copyObjectRequest = new CopyObjectRequest
            {
                SourceBucket = Options.BucketName,
                DestinationBucket = Options.BucketName,
                SourceKey = path(oldSegments),
                DestinationKey = path(newSegments)
            };

            await Client.CopyObjectAsync(copyObjectRequest);

            await deleteAsync(oldSegments);
        }

        protected override Task<bool> DeleteCoreAsync(ResolvedPath resolved, CancellationToken cancellationToken) =>
            deleteAsync(resolved.AbsoluteSegments);

        private async Task<bool> deleteAsync(string[] absolutePathSegments)
        {
            if (GetRelativeSegments(absolutePathSegments).Length == 0)
                throw new InvalidOperationException("The provider root cannot be deleted or modified.");

            var key = path(absolutePathSegments);
            var first = await firstObject(key);
            if (first == null) return false;
            if (first.Key == key)
            {
                var status = (await Client.DeleteObjectAsync(Options.BucketName, key)).HttpStatusCode;
                return status == HttpStatusCode.OK || status == HttpStatusCode.NoContent;
            }

            var deleted = false;
            var prefix = dirPath(absolutePathSegments);
            string continuationToken = null;
            do
            {
                var page = await Client.ListObjectsV2Async(new ListObjectsV2Request()
                {
                    BucketName = Options.BucketName,
                    Prefix = prefix,
                    MaxKeys = MaxDeleteBatchSize,
                    ContinuationToken = continuationToken,
                    RequestPayer = RequestPayer.Requester
                });
                continuationToken = page.NextContinuationToken;
                if (!page.S3Objects.Any()) continue;

                var ret = await Client.DeleteObjectsAsync(new DeleteObjectsRequest()
                {
                    BucketName = Options.BucketName,
                    RequestPayer = RequestPayer.Requester,
                    Quiet = true,
                    Objects = page.S3Objects.Select(e => new KeyVersion() {Key = e.Key}).ToList()
                });
                if (ret.DeleteErrors.Any())
                    throw new IOException(
                        $"Failed to delete {ret.DeleteErrors.Count} object(s) under '{prefix}'. First error: '{ret.DeleteErrors[0].Key}' {ret.DeleteErrors[0].Code} {ret.DeleteErrors[0].Message}");
                deleted = true;
            } while (!string.IsNullOrEmpty(continuationToken));
            return deleted;
        }

        private string path(string[] absolutePathSegments)
        {
            return string.Join("/", absolutePathSegments);
        }

        private string dirPath(string[] absolutePathSegments)
        {
            var p = path(absolutePathSegments);
            return p.Length == 0 ? p : $"{p}/";
        }

        private string fileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var ret = path;
            if (ret.EndsWith("/")) ret = ret.Substring(0, ret.Length - 1);
            if (ret.Contains("/"))
                ret = ret.Substring(ret.LastIndexOf("/") + 1);
            return ret;
        }

        protected override bool SupportsSignedUrl(SignedUrlAccess permission) => permission == SignedUrlAccess.Read;

        protected override Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath resolved, SignedUrlOptions options, CancellationToken cancellationToken)
        {
            var url = getDownloadUrl(resolved.AbsoluteSegments, options.Validity);
            return Task.FromResult(new SignedUrl { Url = new Uri(url), ExpiresOn = DateTimeOffset.UtcNow.Add(options.Validity) });
        }

        private string getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            var request = new GetPreSignedUrlRequest()
            {
                BucketName = Options.BucketName,
                Key = path(absolutePathSegments),
                Expires = DateTime.UtcNow.Add(validity),
                Verb = HttpVerb.GET
            };
            request.ResponseHeaderOverrides.ContentType = "application/octet-stream";
            return Client.GetPreSignedURL(request);
        }
    }
}
