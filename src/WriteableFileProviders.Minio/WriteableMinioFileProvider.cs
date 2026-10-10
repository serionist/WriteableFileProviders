using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace WriteableFileProviders.Minio
{
    public class WriteableMinioFileProvider: WriteableFileProvider<MinioFileProviderOptions>
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
        private const int DeleteBatchSize = 1000;
        private const string RootModificationMessage = "The provider root cannot be deleted or modified.";
        // Shared by all instances. Attached after Build(), which changes the client's timeout and headers.
        private static readonly HttpClient SharedHttpClient =
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(30) };
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        private volatile bool bucketReady;
        private IMinioClient Client { get; }
        public WriteableMinioFileProvider(MinioFileProviderOptions options) : base(options)
        {
            var client = new MinioClient()
                .WithEndpoint(options.HostAndPort)
                .WithCredentials(options.AccessKey, options.SecretKey)
                .WithSSL(options.UseSSL)
                .Build();
            client.Config.HttpClient?.Dispose();
            Client = client.WithHttpClient(SharedHttpClient);
        }

        private async Task<IWriteableFileInfo> getFileInfoAsync(params string[] absolutePathSegments)
        {
            if (isRoot(absolutePathSegments))
                return notFound(absolutePathSegments);
            var key = path(absolutePathSegments);
            try
            {
                var stat = await Client.StatObjectAsync(new StatObjectArgs().WithBucket(Options.BucketName).WithObject(key));
                return legacyInfo(true, stat.Size, key, fileName(key), stat.LastModified, false,
                    GetRelativeSegments(absolutePathSegments));
            }
            catch (ObjectNotFoundException)
            {
            }
            catch (BucketNotFoundException)
            {
                return notFound(absolutePathSegments);
            }

            await foreach (var _ in Client.ListObjectsEnumAsync(listArgs(dirPath(absolutePathSegments), false)))
                return legacyInfo(true, -1, key, fileName(key), DefaultDateTime, true,
                    GetRelativeSegments(absolutePathSegments));
            return notFound(absolutePathSegments);
        }

        private async Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments)
        {
            var prefix = dirPath(absolutePathSegments);
            var exists = false;
            var res = new List<IFileInfo>();
            try
            {
                await foreach (var item in Client.ListObjectsEnumAsync(listArgs(prefix, false)))
                {
                    exists = true;
                    // The client decodes object keys but not common prefixes.
                    var key = item.IsDir ? WebUtility.UrlDecode(item.Key) : item.Key;
                    if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    var name = key.Substring(prefix.Length).TrimEnd('/');
                    if (name.Length == 0 || name.Contains('/'))
                        continue;
                    var segments = GetRelativeSegments(absolutePathSegments.Concat(new[] { name }).ToArray());
                    res.Add(item.IsDir
                        ? legacyInfo(true, -1, prefix + name, name, DefaultDateTime, true, segments)
                        : legacyInfo(true, (long) item.Size, key, name,
                            item.LastModifiedDateTime ?? DefaultDateTime, false, segments));
                }
            }
            catch (Exception e)
            {
                // Some S3-compatible servers omit the bucket name in NoSuchBucket, which the client then fails to map.
                if (e is BucketNotFoundException || await bucketMissingAsync())
                    return new DirectoryContents(false, new IFileInfo[0]);
                throw;
            }

            return new DirectoryContents(exists, res.ToArray());
        }


        private async Task<Stream> readFileAsync(params string[] absolutePathSegments)
        {
            var tmp = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096,
                FileOptions.DeleteOnClose);
            try
            {
                await Client.GetObjectAsync(new GetObjectArgs().WithBucket(Options.BucketName)
                    .WithObject(path(absolutePathSegments))
                    .WithCallbackStream(async (s, ct) => await s.CopyToAsync(tmp, ct)));
                tmp.Position = 0;
                return tmp;
            }
            catch
            {
                await tmp.DisposeAsync();
                throw;
            }
        }

        private async Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
        {
            ensureNotRoot(absolutePathSegments);
            await ensureBucketAsync();

            await using var tmp = new FileStream(Path.GetTempFileName(), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose);
            if (mode == UpdateFileMode.Append)
            {
                var info = await getFileInfoAsync(absolutePathSegments);
                if (info.Exists && !info.IsDirectory)
                    await using (var s = await readFileAsync(absolutePathSegments))
                        await s.CopyToAsync(tmp);
            }
            await contents.CopyToAsync(tmp);
            await tmp.FlushAsync();
            tmp.Position = 0;
            await Client.PutObjectAsync(new PutObjectArgs().WithBucket(Options.BucketName)
                .WithObject(path(absolutePathSegments))
                .WithStreamData(tmp)
                .WithObjectSize(tmp.Length)
                .WithContentType("application/octet-stream"));
        }
        private async Task move(string[] oldSegments, string[] newSegments)
        {
            ensureNotRoot(oldSegments);
            ensureNotRoot(newSegments);
            await ensureBucketAsync();
            await Client.CopyObjectAsync(new CopyObjectArgs().WithBucket(Options.BucketName).WithObject(path(newSegments))
                .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(Options.BucketName).WithObject(path(oldSegments))));
            await Client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Options.BucketName).WithObject(path(oldSegments)));
        }

        private async Task<bool> deleteAsync(params string[] absolutePathSegments)
        {
            ensureNotRoot(absolutePathSegments);
            var info = await getFileInfoAsync(absolutePathSegments);
            if (!info.Exists)
                return false;
            if (!info.IsDirectory)
            {
                await Client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Options.BucketName)
                    .WithObject(path(absolutePathSegments)));
                return true;
            }

            // Every batch comes from a fresh listing, so no continuation token is reused after a delete.
            var prefix = dirPath(absolutePathSegments);
            var deleted = false;
            string previousFirstKey = null;
            while (true)
            {
                var keys = new List<string>(DeleteBatchSize);
                await foreach (var item in Client.ListObjectsEnumAsync(listArgs(prefix, true)))
                {
                    keys.Add(item.Key);
                    if (keys.Count == DeleteBatchSize)
                        break;
                }
                if (keys.Count == 0)
                    return deleted;
                if (keys[0] == previousFirstKey)
                    throw new IOException($"The objects under '{prefix}' could not be deleted.");
                previousFirstKey = keys[0];

                var errors = await Client.RemoveObjectsAsync(new RemoveObjectsArgs().WithBucket(Options.BucketName)
                    .WithObjects(keys));
                if (errors != null && errors.Count > 0)
                    throw new IOException(
                        $"Failed to delete {errors.Count} object(s) under '{prefix}'. First error: '{errors[0].Key}' {errors[0].Code} {errors[0].Message}");
                deleted = true;
            }
        }

        private ListObjectsArgs listArgs(string prefix, bool recursive)
        {
            return new ListObjectsArgs().WithBucket(Options.BucketName).WithPrefix(prefix).WithRecursive(recursive);
        }

        private WriteableFileInfo notFound(string[] absolutePathSegments)
        {
            return legacyInfo(false, -1, path(absolutePathSegments), fileName(path(absolutePathSegments)),
                DefaultDateTime, false, GetRelativeSegments(absolutePathSegments));
        }

        private bool isRoot(string[] absolutePathSegments)
        {
            return GetRelativeSegments(absolutePathSegments ?? new string[0]).Length == 0;
        }

        private void ensureNotRoot(string[] absolutePathSegments)
        {
            if (isRoot(absolutePathSegments))
                throw new InvalidOperationException(RootModificationMessage);
        }

        private string path(string[] absolutePathSegments)
        {
            return string.Join("/", absolutePathSegments);
        }

        private string dirPath(string[] absolutePathSegments)
        {
            return absolutePathSegments.Length == 0 ? "" : $"{path(absolutePathSegments)}/";
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

        private async Task<bool> bucketMissingAsync()
        {
            try
            {
                return !await Client.BucketExistsAsync(new BucketExistsArgs().WithBucket(Options.BucketName));
            }
            catch
            {
                return false;
            }
        }

        private async Task ensureBucketAsync()
        {
            if (bucketReady) return;
            if (!(await Client.BucketExistsAsync(new BucketExistsArgs().WithBucket(Options.BucketName))))
                await Client.MakeBucketAsync(new MakeBucketArgs().WithBucket(Options.BucketName));
            bucketReady = true;
        }

    }
}
