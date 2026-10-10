using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AmazonS3;
using WriteableFileProviders.Minio;

namespace WriteableFileProviders.Tests
{
    // A live suite: runs against a real account with the credentials in testsettings.json. Excluded by TestCategory!=Live.
    [TestClass]
    [TestCategory("Live")]
    public class MinioFileProvider : TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;
        }
        public MinioFileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableMinioFileProvider(new MinioFileProviderOptions(Settings.MinioProvider_RootPath,
                Settings.MinioProvider_HostAndPort, Settings.MinioProvider_AccessKey, Settings.MinioProvider_SecretKey,
                Settings.MinioProvider_BucketName));
        }
    }
}
