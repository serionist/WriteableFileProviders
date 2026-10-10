using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AmazonS3;

namespace WriteableFileProviders.Tests
{
    /// <summary>
    /// The S3 members that take absolute segments refuse anything outside the provider root. Every case fails before a
    /// request is sent, so these run without an endpoint.
    /// </summary>
    [TestClass]
    public class AmazonS3PathTests
    {
        private static WriteableAmazonS3FileProvider Provider(string root = "tenantA") =>
            new WriteableAmazonS3FileProvider(new AmazonS3FileProviderOptions(root, "test", "test", "wfp-test", "us-east-1"));

        private sealed class SignProbe : WriteableAmazonS3FileProvider
        {
            public SignProbe() : base(new AmazonS3FileProviderOptions("tenantA", "test", "test", "wfp-test", "us-east-1")) { }
            public Task<SignedUrl> Sign(string path, TimeSpan validity) =>
                GetSignedUrlCoreAsync(Resolve(path), new SignedUrlOptions { Validity = validity }, default);
        }

        [TestMethod]
        public async Task ReadUrlsArePresignedOfflineUnderTheRoot()
        {
            // Presigning is local: the URL names the key under the root and expires when asked.
            var url = await new SignProbe().Sign("dir/file.txt", TimeSpan.FromMinutes(5));
            StringAssert.Contains(url.Url.AbsolutePath, "tenantA/dir/file.txt");
            StringAssert.Contains(url.Url.Query, "Expires=");
            Assert.IsNotNull(url.ExpiresOn);

            // Through the public API, what S3 cannot sign is refused before any request.
            var provider = Provider();
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = SignedUrlAccess.Write }));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromDays(8) }));
        }

#pragma warning disable CS0618 // the obsolete members are what these tests pin
        [TestMethod]
        public async Task VersionReadOutsideTheRootIsRefused()
        {
            var provider = Provider();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("dir", "file.txt"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("v1", "", "tenantB", "secret.txt"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("v1", "tenantA", "..", "tenantB", "x"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("v1", "tenantA"));
        }

        [TestMethod]
        public async Task ListingOutsideTheRootIsRefused()
        {
            var provider = Provider();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.EnumerateObjects(new string[0], true));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.EnumerateObjects(new[] { "tenantB" }, true));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.EnumerateObjects(new[] { "tenantA", "%2e%2e" }, false));
        }
#pragma warning restore CS0618

        [TestMethod]
        public async Task SinglePathOnTheConcreteTypeGoesThroughValidation()
        {
            // Bound to the base overload, so the '..' segment is refused by the path validation. The old binding took the
            // whole path as a version id and an empty key, which the SDK refused with its own ArgumentException.
            var provider = Provider();
            var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("a/../b.txt"));
            Assert.AreEqual("path", e.ParamName);
        }

        [TestMethod]
        public async Task VersionReadTakesPathsRelativeToTheRoot()
        {
            var provider = Provider();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileVersionAsync("v1"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileVersionAsync("v1", ".."));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileVersionAsync("v1", "a", ""));
        }
    }
}
