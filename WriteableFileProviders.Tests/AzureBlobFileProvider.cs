using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AzureBlob;

namespace WriteableFileProviders.Tests
{
    [TestClass]
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
                ConnectionString = Settings.AzureBlobFileProvider_ConnectionString,
                ContainerName = Settings.AzureBlobFileProvider_ContainerName,
                RootDirectoryPath = Settings.AzureBlobFileProvider_RootPath
            });
        }
    }
}
