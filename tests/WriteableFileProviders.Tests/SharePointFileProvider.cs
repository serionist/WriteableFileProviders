using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.SharePoint;

namespace WriteableFileProviders.Tests
{
    [TestClass]
    public class SharePointFileProvider : TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;
        }
        public SharePointFileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableSharePointFileProvider(new SharePointFileProviderOptions()
            {
                SiteUrl = Settings.SharePointFileProvider_SiteUrl,
                DocumentLibraryName = Settings.SharePointFileProvider_DocumentLibraryName,
                TenantId = Settings.SharePointFileProvider_TenantId,
                ClientId = Settings.SharePointFileProvider_ClientId,
                ClientSecret = Settings.SharePointFileProvider_ClientSecret,
                WorkFolderPath = Settings.SharePointFileProvider_WorkFolderPath
            });
        }
    }
}
