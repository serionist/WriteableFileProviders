using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Physical;

namespace WriteableFileProviders.Tests.Common
{
    [TestClass]
    public class SegmentValidationTests
    {
        private string root;
        private WriteablePhysicalFileProvider provider;

        private class SegmentProvider : WriteablePhysicalFileProvider
        {
            public SegmentProvider(string rootPath) : base(rootPath) { }
            public static string[] Validate(params string[] segments) => validateSegments(segments);
        }

        [TestInitialize]
        public void Init()
        {
            root = Path.Combine(Path.GetTempPath(), "wfp-segments-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            provider = new WriteablePhysicalFileProvider(root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }

        private static MemoryStream Content() => new MemoryStream(new[] { (byte)1 });

        [TestMethod]
        public void SeparatorsInsideSegmentsAreSplit()
        {
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, SegmentProvider.Validate("a/b", "c"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, SegmentProvider.Validate(@"a\b"));
            CollectionAssert.AreEqual(new string[0], SegmentProvider.Validate());
        }

        [TestMethod]
        public async Task SplitSegmentIsWrittenAsNestedPath()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "a/b", "c.txt");
            Assert.IsTrue(File.Exists(Path.Combine(root, "a", "b", "c.txt")));
            var info = (FileInfo)await provider.GetFileInfoAsync("a/b", "c.txt");
            Assert.IsTrue(info.Exists);
            CollectionAssert.AreEqual(new[] { "a", "b", "c.txt" }, info.PathSegments);
        }

        [DataTestMethod]
        [DataRow("my file.txt")]
        [DataRow("árvíztűrő tükörfúrógép.txt")]
        [DataRow("日本語.txt")]
        [DataRow("a..b.txt")]
        [DataRow(".hidden")]
        public async Task AcceptedNames(string name)
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "dir", name);
            Assert.IsTrue((await provider.GetFileInfoAsync("dir", name)).Exists);
            Assert.IsTrue(File.Exists(Path.Combine(root, "dir", name)));
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow(".")]
        [DataRow("..")]
        [DataRow("...")]
        [DataRow(" ")]
        [DataRow(". .")]
        [DataRow("a/../../x")]
        [DataRow("%2e%2e")]
        [DataRow("%252e%252e")]
        [DataRow("a%2F..%2Fb")]
        [DataRow("a\u0001b")]
        [DataRow("a:b")]
        [DataRow("a/")]
        public async Task RejectedSegments(string segment)
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.GetFileInfoAsync(new[] { segment }));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.ReadFileAsync(new[] { "x", segment }));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.GetDirectoryContentsAsync(new[] { segment }));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "x", segment));
            Assert.ThrowsException<ArgumentException>(() => provider.Delete(new[] { "x", segment }));
            Assert.ThrowsException<ArgumentException>(() => provider.Move(new[] { "x", segment }, new[] { "y" }));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "x")));
        }

        [DataTestMethod]
        [DataRow("..")]
        [DataRow("a/../../x")]
        [DataRow("a/./b")]
        [DataRow(@"a\..\b")]
        [DataRow("%2e%2e/x")]
        public async Task RejectedSubpaths(string subpath)
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.GetFileInfoAsync(subpath));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.DeleteAsync(subpath));
            Assert.ThrowsException<ArgumentException>(() => provider.ReadFile(subpath));
        }

        [TestMethod]
        public void RejectionMessageNamesOnlyTheSegment()
        {
            var e = Assert.ThrowsException<ArgumentException>(() => provider.GetFileInfo(new[] { "secret-parent", ".." }));
            StringAssert.Contains(e.Message, "'..'");
            Assert.IsFalse(e.Message.Contains("secret-parent"));
            Assert.IsFalse(e.Message.Contains(root));
        }

        [TestMethod]
        public async Task EmptySegmentDoesNotDeleteParent()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "keep", "f.txt");
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.DeleteAsync(new[] { "keep", "" }));
            Assert.IsTrue(File.Exists(Path.Combine(root, "keep", "f.txt")));
        }

        [TestMethod]
        public async Task RootDeleteIsRefused()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "f.txt");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.DeleteAsync(""));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.DeleteAsync("/"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.DeleteAsync(new string[0]));
            Assert.ThrowsException<InvalidOperationException>(() => provider.Delete(""));
            Assert.ThrowsException<InvalidOperationException>(() => provider.Delete(new string[0]));
            var rootInfo = await provider.GetFileInfoAsync("");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.DeleteAsync(rootInfo));
            Assert.IsTrue(File.Exists(Path.Combine(root, "f.txt")));
        }

        [TestMethod]
        public async Task RootMoveAndUpdateAreRefused()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "f.txt");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.MoveAsync("", "moved"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.MoveAsync("f.txt", ""));
            Assert.ThrowsException<InvalidOperationException>(() => provider.Move("", "moved"));
            var info = await provider.GetFileInfoAsync("f.txt");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.MoveAsync(info, new string[0]));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.UpdateFileAsync("", UpdateFileMode.Overwrite, Content()));
            Assert.ThrowsException<InvalidOperationException>(() => provider.UpdateFile(Content(), UpdateFileMode.Append, new string[0]));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.CreateLocalFileStreamAsync(false, ""));
            Assert.IsTrue(File.Exists(Path.Combine(root, "f.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "moved")));
        }

        [TestMethod]
        public async Task ZeroSegmentReadAndDownloadUrlAreRefused()
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.ReadFileAsync(""));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.ReadFileAsync(new string[0]));
            Assert.ThrowsException<ArgumentException>(() => provider.ReadFile("/"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.CreateLocalFileStreamAsync(true, ""));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.GetDownloadUrlAsync("", TimeSpan.FromMinutes(1)));
            var rootInfo = await provider.GetFileInfoAsync("");
            Assert.ThrowsException<ArgumentException>(() => provider.GetDownloadUrl(rootInfo, TimeSpan.FromMinutes(1)));
        }

        [TestMethod]
        public async Task MoveToItselfKeepsTheFile()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "same", "f.txt");
            await provider.MoveAsync("same/f.txt", "same/f.txt");
            await provider.MoveAsync(new[] { "same", "f.txt" }, new[] { "same/f.txt" });
            provider.Move(await provider.GetFileInfoAsync("same", "f.txt"), new[] { "same", "f.txt" });
            Assert.IsTrue(File.Exists(Path.Combine(root, "same", "f.txt")));
        }

        [TestMethod]
        public async Task ZeroSegmentFileInfoIsNotFound()
        {
            foreach (var info in new[] { await provider.GetFileInfoAsync(""), await provider.GetFileInfoAsync(new string[0]), provider.GetFileInfo("/") })
            {
                Assert.IsFalse(info.Exists);
                Assert.IsFalse(info.IsDirectory);
            }
            Assert.IsTrue((await provider.GetDirectoryContentsAsync("")).Exists);
        }

        [TestMethod]
        public async Task MoveByFileInfoLandsUnderTheRoot()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "src", "a.txt");
            var info = await provider.GetFileInfoAsync("src", "a.txt");
            await provider.MoveAsync(info, new[] { "dst", "b.txt" });
            Assert.IsFalse(File.Exists(Path.Combine(root, "src", "a.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "dst", "b.txt")));
            Assert.IsTrue((await provider.GetFileInfoAsync("dst/b.txt")).Exists);
        }

        [TestMethod]
        public async Task ListingEntriesCanBeReused()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "list", "sub", "f.txt");
            var entries = (await provider.GetDirectoryContentsAsync("list")).ToList();
            var dir = entries.Single(e => e.Name == "sub");
            Assert.IsTrue(dir.IsDirectory);
            Assert.IsTrue(await provider.DeleteAsync(dir));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "list", "sub")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "list")));
        }
    }
}
