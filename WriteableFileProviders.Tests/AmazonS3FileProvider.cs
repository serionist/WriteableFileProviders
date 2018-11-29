using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AmazonS3;

namespace WriteableFileProviders.Tests
{
    [TestClass]
    public class AmazonS3FileProvider:TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;
        }
        public AmazonS3FileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions()
            {
                AccessKey = Settings.AmazonS3FileProvider_AccessKey,
                SecretKey = Settings.AmazonS3FileProvider_SecretKey,
                BucketName = Settings.AmazonS3FileProvider_BucketName,
                BucketRegion = Settings.AmazonS3FileProvider_BucketRegion,
                RootDirectoryPath = Settings.AmazonS3FileProvider_RootPath
            });
        }
    }
}
