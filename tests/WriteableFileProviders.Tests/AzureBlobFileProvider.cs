using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AzureBlob;

namespace WriteableFileProviders.Tests
{
    // A live suite: runs against a real account with the credentials in testsettings.json. Excluded by TestCategory!=Live.
    [TestClass]
    [TestCategory("Live")]
    public class AzureBlobFileProvider:TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;
        }
        public AzureBlobFileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableAzureBlobFileProvider(new AzureFileProviderOptions()
            {
                AccountKey = Settings.AzureBlobFileProvider_AccountKey,
                ContainerName = Settings.AzureBlobFileProvider_ContainerName,
                RootDirectoryPath = Settings.AzureBlobFileProvider_RootPath,
                AccountName = Settings.AzureBlobFileProvider_AccountName
            });
        }
    }
}
