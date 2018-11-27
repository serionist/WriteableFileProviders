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
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableAzureBlobFileProvider(new AzureFileProviderOptions()
            {
                ConnectionString = (string)context.Properties["AzureBlobFileProvider_ConnectionString"],
                ContainerName = (string)context.Properties["AzureBlobFileProvider_ContainerName"],
            });
        }
    }
}
