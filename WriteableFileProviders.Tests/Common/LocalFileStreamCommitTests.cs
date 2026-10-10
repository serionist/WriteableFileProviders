using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Tests.Common
{
    /// <summary>
    /// A writeable local stream commits only to a path under its own provider's root, however the stream was built.
    /// </summary>
    [TestClass]
    public class LocalFileStreamCommitTests
    {
        private class RecordingProvider : WriteableFileProvider<FileProviderOptions>
        {
            public RecordingProvider(string root) : base(new FileProviderOptions { RootDirectoryPath = root }) { }

            public List<string[]> Writes { get; } = new List<string[]>();

            protected override Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments) =>
                Task.FromResult<IFileInfo>(new FileInfo(this, false, -1, null, absolutePathSegments.Last(), DateTimeOffset.MinValue, false, getRelativeSegments(absolutePathSegments)));
            protected override Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments) => throw new NotSupportedException();
            protected override Task<IChangeToken> watchAsync(string filter) => throw new NotSupportedException();
            protected override Task<Stream> readFileAsync(params string[] absolutePathSegments) => throw new NotSupportedException();
            protected override Task<bool> deleteAsync(params string[] absolutePathSegments) => throw new NotSupportedException();
            protected override Task<string> getDownloadUrl(string[] absolutePathSegments, TimeSpan validity) => throw new NotSupportedException();
            protected override Task move(string[] oldSegments, string[] newSegments) => throw new NotSupportedException();

            protected override Task updateFileAsync(Stream contents, UpdateFileMode mode, params string[] absolutePathSegments)
            {
                Writes.Add(absolutePathSegments);
                return Task.CompletedTask;
            }
        }

        private static LocalFileStream HandBuilt(IWriteableFileProvider provider, params string[] segments) =>
            new LocalFileStream(new MemoryStream(new byte[] { 1 }), provider, false, segments);

        [TestMethod]
        public async Task StreamFromTheProviderCommitsToItsTarget()
        {
            var provider = new RecordingProvider("tenantA");
            var stream = await provider.CreateLocalFileStreamAsync(false, "dir", "a.txt");
            await stream.DisposeAsync();
            Assert.AreEqual(1, provider.Writes.Count);
            CollectionAssert.AreEqual(new[] { "tenantA", "dir", "a.txt" }, provider.Writes[0]);
        }

        [DataTestMethod]
        [DataRow("tenantB", "x.txt")]
        [DataRow("x.txt")]
        [DataRow("tenantA", "..")]
        [DataRow("tenantA", "%2e%2e")]
        [DataRow("tenantA", "")]
        public async Task HandBuiltStreamOutsideTheRootIsRefused(params string[] segments)
        {
            var provider = new RecordingProvider("tenantA");
            var stream = HandBuilt(provider, segments);
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => stream.DisposeAsync());
            Assert.AreEqual(0, provider.Writes.Count);
        }

        [TestMethod]
        public async Task HandBuiltStreamOnTheRootIsRefused()
        {
            var provider = new RecordingProvider("tenantA");
            var stream = HandBuilt(provider, "tenantA");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => stream.DisposeAsync());
            Assert.AreEqual(0, provider.Writes.Count);
        }

        [TestMethod]
        public async Task StreamOfAnotherProviderIsRefused()
        {
            var owner = new RecordingProvider("tenantA");
            var other = new RecordingProvider("tenantA");
            var stream = HandBuilt(owner, "tenantA", "x.txt");
            stream.SkipReUploadOnDispose = true;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => other.OnLocalFileStreamDisposingAsync(stream));
            Assert.AreEqual(0, other.Writes.Count);
            await stream.DisposeAsync();
            Assert.AreEqual(0, owner.Writes.Count);
        }

        [TestMethod]
        public async Task HandBuiltStreamUnderTheRootCommits()
        {
            var provider = new RecordingProvider("tenantA");
            await HandBuilt(provider, "tenantA", "dir/x.txt").DisposeAsync();
            Assert.AreEqual(1, provider.Writes.Count);
            CollectionAssert.AreEqual(new[] { "tenantA", "dir", "x.txt" }, provider.Writes[0]);
        }
    }
}
