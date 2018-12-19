using System;
using System.Collections.Generic;
using System.Text;
using Amazon.S3;

namespace WriteableFileProviders.AmazonS3
{
    public sealed class AmazonS3FileProviderOptions: FileProviderOptions
    {
        public AmazonS3FileProviderOptions(string rootDirectory, string accessKey, string secretKey, string bucketName, string bucketRegion, S3StorageClass storageClassForNewFiles = null)
        {
            RootDirectoryPath = rootDirectory;
            IsAnonymus = string.IsNullOrEmpty(accessKey);
            AccessKey = accessKey;
            SecretKey = secretKey;
            BucketName = bucketName;
            BucketRegion = bucketRegion;
            StorageClassForNewFiles = storageClassForNewFiles ?? S3StorageClass.Standard;
        }
        public AmazonS3FileProviderOptions(string rootDirectory, string accessKey, string secretKey, string bucketName, S3Region bucketRegion, S3StorageClass storageClassForNewFiles = null):this(rootDirectory, accessKey, secretKey, bucketName, bucketRegion?.Value, storageClassForNewFiles)
        {

        }
        public AmazonS3FileProviderOptions(string rootDirectory, string bucketName, string bucketRegion, S3StorageClass storageClassForNewFiles = null): this (rootDirectory, null, null, bucketName, bucketRegion, storageClassForNewFiles)
        {

        }
        public AmazonS3FileProviderOptions(string rootDirectory, string bucketName, S3Region bucketRegion, S3StorageClass storageClassForNewFiles = null) : this(rootDirectory, null, null, bucketName, bucketRegion?.Value, storageClassForNewFiles)
        {

        }
        public AmazonS3FileProviderOptions() { }

        public bool IsAnonymus { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
      
        public string BucketName { get; set; }
        public string BucketRegion { get; set; }
        public S3StorageClass StorageClassForNewFiles { get; set; } = S3StorageClass.Standard;
    }
}
