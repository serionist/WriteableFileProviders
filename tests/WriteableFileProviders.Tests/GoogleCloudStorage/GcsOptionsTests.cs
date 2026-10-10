using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.GoogleCloudStorage;

namespace WriteableFileProviders.Tests.GoogleCloudStorage
{
    [TestClass]
    public class GcsOptionsTests
    {
        private const string ClientEmail = "wfp-test@example-project.iam.gserviceaccount.com";
        private static readonly string pem = createPem();

        private static string createPem()
        {
            using (var rsa = RSA.Create(2048))
                return rsa.ExportPkcs8PrivateKeyPem().Replace("\r\n", "\n");
        }

        private static string escapedLf => pem.Replace("\n", "\\n");
        private static string escapedCrLf => pem.Replace("\n", "\\r\\n");
        private static string escapedCr => pem.Replace("\n", "\\r");

        private static GoogleCloudStorageFileProviderOptions options(string privateKey, string root = null)
        {
            return new GoogleCloudStorageFileProviderOptions("wfp-test-bucket", ClientEmail, privateKey, "example-project", root);
        }

        [TestMethod]
        public void NormalizePem_LeavesEmptyAndValidKeysUnchanged()
        {
            Assert.IsNull(WriteableGoogleCloudStorageFileProvider.NormalizePem(null));
            Assert.AreEqual("", WriteableGoogleCloudStorageFileProvider.NormalizePem(""));
            Assert.AreSame(pem, WriteableGoogleCloudStorageFileProvider.NormalizePem(pem));
            var crlf = pem.Replace("\n", "\r\n");
            Assert.AreEqual(crlf, WriteableGoogleCloudStorageFileProvider.NormalizePem(crlf));
        }

        [TestMethod]
        public void NormalizePem_TurnsEscapedNewlinesIntoNewlines()
        {
            Assert.AreEqual(pem, WriteableGoogleCloudStorageFileProvider.NormalizePem(escapedLf));
            Assert.AreEqual(pem, WriteableGoogleCloudStorageFileProvider.NormalizePem(escapedCrLf));
            Assert.AreEqual(pem, WriteableGoogleCloudStorageFileProvider.NormalizePem(escapedCr));
        }

        [TestMethod]
        public void NormalizePem_IsIdempotent()
        {
            foreach (var key in new[] { pem, escapedLf, escapedCrLf, escapedCr })
            {
                var once = WriteableGoogleCloudStorageFileProvider.NormalizePem(key);
                Assert.AreEqual(once, WriteableGoogleCloudStorageFileProvider.NormalizePem(once));
            }
        }

        [TestMethod]
        public void EscapedKey_IsRejectedByTheCredentialStackWithoutNormalisation()
        {
            Assert.ThrowsExactly<FormatException>(() => new ServiceAccountCredential.Initializer(ClientEmail).FromPrivateKey(escapedLf));
        }

        [TestMethod]
        public void Provider_BuildsWithRealAndEscapedKeys()
        {
            foreach (var key in new[] { pem, pem.Replace("\n", "\r\n"), escapedLf, escapedCrLf, escapedCr })
                Assert.IsNotNull(new WriteableGoogleCloudStorageFileProvider(options(key)));
        }

        [TestMethod]
        public void Provider_DoesNotModifyThePrivateKeyOption()
        {
            var provider = new WriteableGoogleCloudStorageFileProvider(options(escapedLf));
            Assert.AreEqual(escapedLf, provider.Options.PrivateKey);
            Assert.AreEqual(escapedLf, (string)provider.ToJson()["PrivateKey"]);

            var restored = (WriteableGoogleCloudStorageFileProvider)WriteableFileProviderSerializer.FromJson(
                WriteableFileProviderSerializer.ToJson(provider), typeof(WriteableGoogleCloudStorageFileProvider).Assembly);
            Assert.AreEqual(escapedLf, restored.Options.PrivateKey);
        }

        [TestMethod]
        public async Task Delete_ProviderRootIsRefusedWithoutNetwork()
        {
            var provider = new Probe(options(escapedLf));
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAbsolute());
            Assert.AreEqual("The provider root cannot be deleted or modified.", ex.Message);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAsync(""));

            var rooted = new Probe(options(escapedLf, "base/inner"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rooted.DeleteAbsolute("base", "inner"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rooted.DeleteAsync(""));
        }

        [TestMethod]
        public async Task FileInfo_ZeroSegmentsIsNotFoundWithoutNetwork()
        {
            var info = await new Probe(options(escapedLf)).FileInfoAbsolute();
            Assert.IsFalse(info.Exists);
        }

        private class Probe : WriteableGoogleCloudStorageFileProvider
        {
            public Probe(GoogleCloudStorageFileProviderOptions options) : base(options) { }
            private ResolvedPath Raw(string[] absolute) => new ResolvedPath(Options.RootDirectorySegments, GetRelativeSegments(absolute));
            public Task<bool> DeleteAbsolute(params string[] absolutePathSegments) => DeleteCoreAsync(Raw(absolutePathSegments), default);
            public Task<IWriteableFileInfo> FileInfoAbsolute(params string[] absolutePathSegments) => GetFileInfoCoreAsync(Raw(absolutePathSegments), default);
        }
    }
}
