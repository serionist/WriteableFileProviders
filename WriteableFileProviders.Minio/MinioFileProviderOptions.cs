using System;
using System.Collections.Generic;
using System.Text;

namespace WriteableFileProviders.Minio
{
    public class MinioFileProviderOptions: FileProviderOptions
    {
        public MinioFileProviderOptions(string rootDirectory, string hostAndPort, string accessKey, string secretKey, string bucketName)
        {
            RootDirectoryPath = rootDirectory;
            AccessKey = accessKey;
            SecretKey = secretKey;
            BucketName = bucketName;
            HostAndPort = hostAndPort;
        }
        public MinioFileProviderOptions() { }

        public string HostAndPort { get; set; }
        /// <summary>
        /// Connects over HTTPS when true. Defaults to false for compatibility; set it to true in production.
        /// </summary>
        public bool UseSSL { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string BucketName { get; set; }
    }
}
