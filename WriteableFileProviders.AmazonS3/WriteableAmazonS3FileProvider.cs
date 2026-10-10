using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

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

        protected override async Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            var key = path(absolutePathSegments);
            var relativeSegments = getRelativeSegments(absolutePathSegments);
            if (string.IsNullOrEmpty(key))
                return new FileInfo(this, false, -1, key, fileName(key), DefaultDateTime, false, relativeSegments);

            //an exact key is always listed first under its own prefix
            var first = await firstObject(key);
            if (first != null && first.Key == key)
                return new FileInfo(this, true, first.Size, key, fileName(key), first.LastModified, false, relativeSegments);

            var isDirectory = first != null
                              && (first.Key.StartsWith(key + "/", StringComparison.Ordinal) || await firstObject(key + "/") != null);
            return new FileInfo(this, false, -1, key, fileName(key), DefaultDateTime, isDirectory, relativeSegments);
        }

        protected override async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var ret = await EnumerateObjects(absolutePathSegments, false);

            return new DirectoryContents(ret.Any(), ret.ToArray());
        }

        public async Task<List<IFileInfo>> EnumerateObjects(string[] absolutePathSegments, bool deep)
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

                    ret.Add(new FileInfo(this, true, r.Size, r.Key, fileName(r.Key), r.LastModified, false, getRelativeSegments(absolutePathSegments.Concat(relativeParts).ToArray())));
                }

                foreach (var p in results.CommonPrefixes)
                {
                    var d = p.Substring(dirPath.Length, p.Length - dirPath.Length - 1);
                    if (d.Length > 0 && seenDirnames.Add(d))
                        dirnames.Add(d);
                }
            } while (!string.IsNullOrEmpty(continuationToken));
            foreach (var d in dirnames)
                ret.Add(new FileInfo(this, true, -1, dirPath + d, d, DefaultDateTime, true, getRelativeSegments(absolutePathSegments.Concat(new[] { d }).ToArray())));

            return ret;
        }

        protected override async Task<IChangeToken> watchAsync(string filter)
        {
            throw new PlatformNotSupportedException("Watch is not supported in WriteableAmazonS3FileProvider");
        }

        public async Task<Stream> ReadFileAsync(string versionId, params string[] absolutePathSegments)
        {

            if (string.IsNullOrEmpty(versionId))
                return (await Client.GetObjectAsync(Options.BucketName, path(absolutePathSegments))).ResponseStream;
            else
                return (await Client.GetObjectAsync(Options.BucketName, path(absolutePathSegments), versionId)).ResponseStream;
        }
        protected override async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            return await ReadFileAsync(null, absolutePathSegments);
        }

        protected override async Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
        {

            if (mode == UpdateFileMode.Append)
            {
                await using var str = await createLocalFileStreamAsync(false, absolutePathSegments);
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
        protected override async Task move(string[] oldSegments, string[] newSegments)
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

        protected override async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            if (getRelativeSegments(absolutePathSegments).Length == 0)
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

        protected override Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity)
        {
            var request = new GetPreSignedUrlRequest()
            {
                BucketName = Options.BucketName,
                Key = path(absolutePathSegments),
                Expires = DateTime.UtcNow.Add(validity),
                Verb = HttpVerb.GET
            };
            request.ResponseHeaderOverrides.ContentType = "application/octet-stream";
            return Task.FromResult(Client.GetPreSignedURL(request));
        }
    }
}
