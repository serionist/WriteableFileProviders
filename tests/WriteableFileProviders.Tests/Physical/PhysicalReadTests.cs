using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Physical;

namespace WriteableFileProviders.Tests.Physical
{
    [TestClass]
    public class PhysicalReadTests
    {
        private string root;
        private TestProvider provider;

        private class TestProvider : WriteablePhysicalFileProvider
        {
            public TestProvider(string rootPath) : base(rootPath) { }
            private ResolvedPath Raw(string[] absolute) => new ResolvedPath(Options.RootDirectorySegments, GetRelativeSegments(absolute));
            public string[] Absolute(params string[] relative) => Options.RootDirectorySegments.Concat(relative).ToArray();
            public Task<Stream> ReadAbsoluteAsync(params string[] absolute) => ReadFileCoreAsync(Raw(absolute), default);
            public Task<bool> DeleteAbsoluteAsync(params string[] absolute) => DeleteCoreAsync(Raw(absolute), default);
            public Task UpdateAbsoluteAsync(Stream contents, params string[] absolute) => UpdateFileCoreAsync(Raw(absolute), contents, UpdateFileOptions.Overwrite, default);
            public async Task<IDirectoryContents> ListAbsoluteAsync(params string[] absolute)
            {
                var entries = new System.Collections.Generic.List<Microsoft.Extensions.FileProviders.IFileInfo>();
                await foreach (var entry in EnumerateCoreAsync(Raw(absolute), default))
                    entries.Add(entry);
                return new DirectoryContents(entries.Count > 0, entries);
            }
        }

        [TestInitialize]
        public void Init()
        {
            root = Path.Combine(Path.GetTempPath(), "wfp-physical-" + Guid.NewGuid().ToString("N"), "root");
            Directory.CreateDirectory(root);
            provider = new TestProvider(root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            var parent = Path.GetDirectoryName(root);
            if (Directory.Exists(parent))
                Directory.Delete(parent, true);
        }

        private static MemoryStream Content() => new MemoryStream(new[] { (byte)7, (byte)8 });

        [TestMethod]
        public async Task MissingReadThrowsAndCreatesNothing()
        {
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("missing", "file.txt"));
            Assert.ThrowsExactly<FileNotFoundException>(() => provider.ReadFile("missing/file.txt"));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("file.txt"));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "missing")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "file.txt")));
        }

        [TestMethod]
        public async Task MissingReadOnlyLocalStreamThrowsAndCreatesNothing()
        {
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.CreateLocalFileStreamAsync(true, "missing/file.txt"));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "missing")));
        }

        [TestMethod]
        public async Task DirectoryReadThrows()
        {
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("dir"));
        }

        [TestMethod]
        public async Task PresentReadWorks()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "present", "file.txt");
            using (var s = await provider.ReadFileAsync("present/file.txt"))
            using (var m = new MemoryStream())
            {
                await s.CopyToAsync(m);
                CollectionAssert.AreEqual(new[] { (byte)7, (byte)8 }, m.ToArray());
            }
            using (var local = await provider.CreateLocalFileStreamAsync(true, "present/file.txt"))
                Assert.AreEqual(2, local.Length);
        }

        [TestMethod]
        public async Task WriteCreatesFolders()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "w1", "w2", "file.txt");
            Assert.IsTrue(File.Exists(Path.Combine(root, "w1", "w2", "file.txt")));
            using (var local = await provider.CreateLocalFileStreamAsync(false, "l1/l2/file.txt"))
                local.Write(new[] { (byte)1 }, 0, 1);
            Assert.IsTrue(File.Exists(Path.Combine(root, "l1", "l2", "file.txt")));
        }

        [TestMethod]
        public async Task MoveCreatesTargetFolder()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "file.txt");
            await provider.MoveAsync("file.txt", "new/folder/file.txt");
            Assert.IsTrue(File.Exists(Path.Combine(root, "new", "folder", "file.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "file.txt")));
        }

        [TestMethod]
        public async Task DeleteOfRootIsRefused()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "file.txt");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAsync(""));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAbsoluteAsync(provider.Absolute()));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAbsoluteAsync(provider.Absolute("sub", "..")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "file.txt")));
        }

        [TestMethod]
        public async Task ContainmentEscapeIsRefused()
        {
            var outside = Path.Combine(Path.GetDirectoryName(root), "outside.txt");
            File.WriteAllBytes(outside, new byte[] { 1 });
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadAbsoluteAsync(provider.Absolute("..", "outside.txt")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAbsoluteAsync(provider.Absolute("..", "outside.txt")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAbsoluteAsync(provider.Absolute("..")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateAbsoluteAsync(Content(), provider.Absolute("..", "escaped.txt")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ListAbsoluteAsync(provider.Absolute("..")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadAbsoluteAsync(provider.Absolute("..", Path.GetFileName(root) + "-sibling", "f.txt")));
            Assert.IsTrue(File.Exists(outside));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(root), "escaped.txt")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("..", "outside.txt"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("c:", "outside.txt"));
        }

        [TestMethod]
        public async Task DirectoryEntriesKeepFileSystemTimestamps()
        {
            await provider.UpdateFileAsync(Content(), UpdateFileMode.Overwrite, "ts", "sub", "file.txt");
            var dir = (await provider.GetDirectoryContentsAsync("ts")).Single(e => e.Name == "sub");
            Assert.IsTrue(dir.IsDirectory);
            var expected = new DateTimeOffset(Directory.GetLastWriteTimeUtc(Path.Combine(root, "ts", "sub")));
            Assert.IsTrue((dir.LastModified - expected).Duration() < TimeSpan.FromSeconds(2));
            Assert.IsTrue(dir.LastModified.Year > 1970);
        }
    }
}
