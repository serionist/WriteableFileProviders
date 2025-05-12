using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box;

namespace WriteableFileProviders.Tests
{
    [TestClass]
    public class BoxFileProvider : TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;
        }
        public BoxFileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            return new WriteableBoxFileProvider(new BoxFileProviderOptions()
            {
                ClientId = Settings.BoxFileProvider_ClientId,
                ClientSecret = Settings.BoxFileProvider_ClientSecret,
                EnterpriseId = Settings.BoxFileProvider_EnterpriseId,
                PublicKeyId = Settings.BoxFileProvider_PublicKeyId,
                PrivateKey = Settings.BoxFileProvider_PrivateKey,
                Passphrase = Settings.BoxFileProvider_Passphrase,
                RootFolderId = Settings.BoxFileProvider_RootFolderId
            });
        }
    }
}
