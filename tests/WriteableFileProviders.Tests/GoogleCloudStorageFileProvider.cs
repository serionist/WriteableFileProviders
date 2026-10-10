using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.GoogleCloudStorage;

namespace WriteableFileProviders.Tests
{
    [TestClass]
    public class GoogleCloudStorageFileProvider : TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;
        }
        public GoogleCloudStorageFileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableGoogleCloudStorageFileProvider(new GoogleCloudStorageFileProviderOptions()
            {
                BucketName = Settings.GoogleCloudStorageFileProvider_BucketName,
                ClientEmail = Settings.GoogleCloudStorageFileProvider_ClientEmail,
                PrivateKey = Settings.GoogleCloudStorageFileProvider_PrivateKey,
                ProjectId = Settings.GoogleCloudStorageFileProvider_ProjectId,
                RootDirectoryPath = Settings.GoogleCloudStorageFileProvider_RootPath
            });
        }
    }
}
