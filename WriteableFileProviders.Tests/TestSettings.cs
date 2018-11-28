using System;
using System.Collections.Generic;
using System.Text;

namespace WriteableFileProviders.Tests
{
    public class TestSettings
    {
        public string PhysicalFileProvider_RootPath { get; set; }
        public string AzureBlobFileProvider_ConnectionString { get; set; }
        public string AzureBlobFileProvider_ContainerName { get; set; }
        public string AmazonS3FileProvider_AccessKey { get; set; }
        public string AmazonS3FileProvider_SecretKey { get; set; }
        public string AmazonS3FileProvider_BucketName { get; set; }
        public string AmazonS3FileProvider_BucketRegion { get; set; }
    }
}
