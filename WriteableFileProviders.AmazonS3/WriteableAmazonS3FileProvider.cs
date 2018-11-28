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
        private readonly DateTime DefaultDateTime = new DateTime(1970, 1, 1, 0, 0, 0);
        AmazonS3Client Client { get; }
        public WriteableAmazonS3FileProvider(AmazonS3FileProviderOptions options) : base(options)
        {
            AWSCredentials credentials = Options.IsAnonymus
                ? (AWSCredentials) new AnonymousAWSCredentials()
                : new BasicAWSCredentials(Options.AccessKey, options.SecretKey);
            Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(Options.BucketRegion));
        }

        private async Task<string> getLatestVersion(string[] pathSegments)
        {
            var list = await Client.ListVersionsAsync(Options.BucketName, path(pathSegments));
            return list.Versions
                ?.Find(e => e.IsLatest)?.VersionId;
        }

        public override async Task<IFileInfo> GetFileInfoAsync(params string[] pathSegments)
        {
            try
            {
                //try to enumerate to see if dir
                var e  = await Client.ListObjectsV2Async(new ListObjectsV2Request()
                {
                    BucketName = Options.BucketName,
                    RequestPayer = RequestPayer.Requester,
                    ContinuationToken = null,
                    Prefix = path(pathSegments),
                    MaxKeys = 2
                });
                if (e.S3Objects.Count != 1) //path is directory, return not found
                    return new FileInfo(this, false, -1, path(pathSegments), fileName(path(pathSegments)), DefaultDateTime, false);
                var latestVersion = await getLatestVersion(pathSegments);
                var file = await Client.GetObjectAsync(Options.BucketName, path(pathSegments), latestVersion);
               return new FileInfo(this, true, file.ContentLength, path(pathSegments), fileName(path(pathSegments)), file.LastModified, false);

            }
            catch (AmazonS3Exception e)
            {
                //file not found
                if (e.ErrorCode == "NoSuchKey")
                    return new FileInfo(this, false, -1, path(pathSegments), fileName(path(pathSegments)), DefaultDateTime, false);
                   
                
                throw;
            }
        }

        public override async Task<IDirectoryContents> GetDirectoryContentsAsync(params string[] pathSegments)
        {
            var ret = await EnumerateObjects(pathSegments, false);

            return new DirectoryContents(ret.Any(), ret.ToArray());
        }

        public async Task<List<IFileInfo>> EnumerateObjects(string[] pathSegments, bool deep)
        {
            var ret = new List<IFileInfo>();
            var dirnames = new List<string>();
            ListObjectsV2Response results;
            var dirPath = this.dirPath(pathSegments);
            string continuationToken = null;
            do
            {
                results = await Client.ListObjectsV2Async(new ListObjectsV2Request()
                {
                    BucketName = Options.BucketName,
                    Prefix = dirPath,
                    ContinuationToken = continuationToken,
                    RequestPayer = RequestPayer.Requester
                });
                continuationToken = results.NextContinuationToken;
                
                foreach (var r in results.S3Objects)
                {
                    var relativePath = r.Key.Substring(dirPath.Length);
                    //lower level file/dir
                    if (relativePath.Contains("/"))
                    {
                        var dirName = relativePath.Substring(0, relativePath.IndexOf("/"));
                        if (!dirnames.Contains(dirName)) dirnames.Add(dirName);
                        if (!deep) continue;
                    }
                    
                    ret.Add(new FileInfo(this, true, r.Size, r.Key, fileName(r.Key), r.LastModified, false));
                }
            } while (!string.IsNullOrEmpty(results.NextContinuationToken));
            foreach (var d in dirnames)
                if (!ret.Exists(e=>e.PhysicalPath == d && e.IsDirectory))
                    ret.Add(new FileInfo(this, true, -1, dirPath + d, d, DefaultDateTime, true));
            
            return ret;
        }

        protected override async Task<IChangeToken> watchAsync(string filter)
        {
            throw new NotImplementedException();
        }

        public async Task<Stream> ReadFileAsync(string versionId, params string[] pathSegments)
        {
           
            if (string.IsNullOrEmpty(versionId))
                return (await Client.GetObjectAsync(Options.BucketName, path(pathSegments))).ResponseStream;
            else
                return (await Client.GetObjectAsync(Options.BucketName, path(pathSegments), versionId)).ResponseStream;
        }
        public override async Task<Stream> ReadFileAsync(params string[] pathSegments)
        {
           
            return await ReadFileAsync(await getLatestVersion(pathSegments), pathSegments);
            
        }

        public override async Task UpdateFileAsync(Stream contents, params string[] pathSegments)
        {
            var a = await Client.PutObjectAsync(new PutObjectRequest()
            {
                InputStream = contents,
                BucketName = Options.BucketName,
                AutoCloseStream = false,
                AutoResetStreamPosition = true,
                Key = path(pathSegments),
                RequestPayer = RequestPayer.Requester,
                StorageClass = Options.StorageClassForNewFiles,
              
                
            });
            
        }

        public override async Task<bool> DeleteAsync(params string[] pathSegments)
        {
            var info = await GetFileInfoAsync(pathSegments);
            if (info.Exists)
                return (await Client.DeleteObjectAsync(Options.BucketName, path(pathSegments))).HttpStatusCode ==
                    HttpStatusCode.OK;

            var listedObjects = await EnumerateObjects(pathSegments, true);
            if (!listedObjects.Any()) return true;
            var ret = await Client.DeleteObjectsAsync(new DeleteObjectsRequest()
            {
                BucketName = Options.BucketName,
                RequestPayer = RequestPayer.Requester,
                Objects = listedObjects.Select(e => new KeyVersion() {Key = e.PhysicalPath}).ToList()
            });
            return !ret.DeleteErrors.Any() && ret.DeletedObjects.Any();
        }
        
        private string path(string[] pathSegments)
        {
            return string.Join("/", pathSegments);
        }

        private string dirPath(string[] pathSegments)
        {
            return $"{path(pathSegments)}/";
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

     
    }
}
