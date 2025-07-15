namespace WriteableFileProviders.Tests
{
    public class TestSettings
    {
        public string PhysicalFileProvider_RootPath { get; set; }
        public string AzureBlobFileProvider_AccountKey { get; set; }
        public string AzureBlobFileProvider_AccountName { get; set; }
        public string AzureBlobFileProvider_ContainerName { get; set; }
        public string AzureBlobFileProvider_RootPath { get; set; }
        public string AmazonS3FileProvider_AccessKey { get; set; }
        public string AmazonS3FileProvider_SecretKey { get; set; }
        public string AmazonS3FileProvider_BucketName { get; set; }
        public string AmazonS3FileProvider_BucketRegion { get; set; }
        public string MinioProvider_RootPath { get; set; }
        public string MinioProvider_HostAndPort { get; set; }
        public string MinioProvider_AccessKey { get; set; }
        public string MinioProvider_SecretKey { get; set; }
        public string MinioProvider_BucketName { get; set; }
        public string BoxFileProvider_ClientId { get; set; }
        public string BoxFileProvider_ClientSecret { get; set; }
        public string BoxFileProvider_EnterpriseId { get; set; }
        public string BoxFileProvider_PublicKeyId { get; set; }
        public string BoxFileProvider_PrivateKey { get; set; }
        public string BoxFileProvider_Passphrase { get; set; }
        public string BoxFileProvider_RootFolderId { get; set; } = "0";
        public string SharePointFileProvider_SiteUrl { get; set; }
        public string SharePointFileProvider_DocumentLibraryName { get; set; } = "Documents";
        public string SharePointFileProvider_TenantId { get; set; }
        public string SharePointFileProvider_ClientId { get; set; }
        public string SharePointFileProvider_ClientSecret { get; set; }

    }
}
