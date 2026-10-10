using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Azure;
using Azure.Storage.Blobs.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AzureBlob;

namespace WriteableFileProviders.Tests.AzureBlob
{
    [TestClass]
    public class AzureBlobProviderTests
    {
        private static readonly string containerName = Azurite.NewContainerName();

        [ClassCleanup]
        public static async Task Cleanup()
        {
            await Azurite.DeleteContainer(containerName);
        }

        private static string NewDir() => $"dir{Guid.NewGuid():N}";

        private static async Task Write(IWriteableFileProvider provider, string path, string content)
        {
            await provider.UpdateFileAsync(path, UpdateFileMode.Overwrite, new MemoryStream(Encoding.UTF8.GetBytes(content)));
        }

        private static async Task<string> Read(Stream stream)
        {
            using (stream)
            using (var reader = new StreamReader(stream))
                return await reader.ReadToEndAsync();
        }

        [TestMethod]
        public async Task Listing_ReportsDirectFilesAndSubfolders()
        {
            var provider = Azurite.CreateProvider(containerName);
            var dir = NewDir();
            await Write(provider, $"{dir}/a/b/c.txt", "deep");
            await Write(provider, $"{dir}/x.txt", "hello");

            var contents = await provider.GetDirectoryContentsAsync(dir);
            var entries = contents.ToList();

            Assert.IsTrue(contents.Exists);
            CollectionAssert.AreEquivalent(new[] { "a", "x.txt" }, entries.Select(e => e.Name).ToArray());
            var a = entries.Single(e => e.Name == "a");
            Assert.IsTrue(a.IsDirectory);
            Assert.IsTrue(a.Exists);
            Assert.AreEqual(-1, a.Length);
            var x = entries.Single(e => e.Name == "x.txt");
            Assert.IsFalse(x.IsDirectory);
            Assert.IsTrue(x.Exists);
            Assert.AreEqual(5, x.Length);
            Assert.IsTrue(x.LastModified > DateTimeOffset.UtcNow.AddHours(-1));

            var nested = (await provider.GetDirectoryContentsAsync($"{dir}/a")).ToList();
            Assert.AreEqual(1, nested.Count);
            Assert.AreEqual("b", nested[0].Name);
            Assert.IsTrue(nested[0].IsDirectory);
        }

        [TestMethod]
        public async Task Listing_PathSegmentsAreRelativeAndRoundTrip()
        {
            var root = $"root{Guid.NewGuid():N}/sub";
            var provider = Azurite.CreateProvider(containerName, root);
            var dir = NewDir();
            await Write(provider, $"{dir}/a/b/c.txt", "deep");
            await Write(provider, $"{dir}/x.txt", "hello");

            var entries = (await provider.GetDirectoryContentsAsync(dir)).Cast<WriteableFileInfo>().ToList();
            var x = entries.Single(e => e.Name == "x.txt");
            var a = entries.Single(e => e.Name == "a");
            CollectionAssert.AreEqual(new[] { dir, "x.txt" }, x.Path.Segments.ToArray());
            CollectionAssert.AreEqual(new[] { dir, "a" }, a.Path.Segments.ToArray());

            Assert.AreEqual("hello", await Read(await provider.ReadFileAsync(x)));
            Assert.IsTrue((await provider.GetFileInfoAsync(x.Path.Segments.ToArray())).Exists);
            var b = (await provider.GetDirectoryContentsAsync(a.Path.Segments.ToArray())).Cast<WriteableFileInfo>().Single();
            CollectionAssert.AreEqual(new[] { dir, "a", "b" }, b.Path.Segments.ToArray());
            var c = (await provider.GetDirectoryContentsAsync(b.Path.Segments.ToArray())).Cast<WriteableFileInfo>().Single();
            CollectionAssert.AreEqual(new[] { dir, "a", "b", "c.txt" }, c.Path.Segments.ToArray());
            Assert.AreEqual("deep", await Read(await provider.ReadFileAsync(c)));
        }

        [TestMethod]
        public async Task Listing_ContainerRootWithoutRootFolder()
        {
            var container = Azurite.NewContainerName();
            try
            {
                var provider = Azurite.CreateProvider(container);
                Assert.IsFalse((await provider.GetDirectoryContentsAsync("")).Exists);
                await Write(provider, "top.txt", "top");
                await Write(provider, "folder/inner.txt", "inner");

                var contents = await provider.GetDirectoryContentsAsync("");
                Assert.IsTrue(contents.Exists);
                var entries = contents.Cast<WriteableFileInfo>().ToList();
                CollectionAssert.AreEquivalent(new[] { "top.txt", "folder" }, entries.Select(e => e.Name).ToArray());
                CollectionAssert.AreEqual(new[] { "folder" }, entries.Single(e => e.IsDirectory).Path.Segments.ToArray());
                CollectionAssert.AreEqual(new[] { "top.txt" }, entries.Single(e => !e.IsDirectory).Path.Segments.ToArray());
            }
            finally
            {
                await Azurite.DeleteContainer(container);
            }
        }

        [TestMethod]
        public async Task Listing_SkipsEmptyNamesFromDoubleSlash()
        {
            var provider = Azurite.CreateProvider(containerName);
            var dir = NewDir();
            await Write(provider, $"{dir}/ok.txt", "ok");
            await Azurite.Container(containerName).GetBlobClient($"{dir}//odd.txt").UploadAsync(new BinaryData("odd"));

            var contents = await provider.GetDirectoryContentsAsync(dir);
            Assert.IsTrue(contents.Exists);
            CollectionAssert.AreEqual(new[] { "ok.txt" }, contents.Select(e => e.Name).ToArray());
        }

        [TestMethod]
        public async Task Reads_DoNotCreateContainer()
        {
            var container = Azurite.NewContainerName();
            var provider = Azurite.CreateProvider(container);
            var client = Azurite.Container(container);
            try
            {
                Assert.IsFalse((await provider.GetFileInfoAsync("missing.txt")).Exists);
                Assert.IsFalse((await provider.GetDirectoryContentsAsync("dir")).Exists);
                Assert.IsFalse((await provider.GetDirectoryContentsAsync("")).Exists);
                Assert.IsFalse(await provider.DeleteAsync("missing.txt"));
                Assert.IsFalse(await provider.DeleteAsync("dir"));
                Assert.IsNotNull(await provider.GetDownloadUrlAsync("missing.txt", TimeSpan.FromMinutes(5)));
                var ex = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => provider.ReadFileAsync("missing.txt"));
                Assert.AreEqual(404, ex.Status);

                Assert.IsFalse((await client.ExistsAsync()).Value, "A read path created the container");

                await Write(provider, "created.txt", "x");
                Assert.IsTrue((await client.ExistsAsync()).Value, "The write path did not create the container");
            }
            finally
            {
                await Azurite.DeleteContainer(container);
            }
        }

        [TestMethod]
        public async Task DeleteDirectory_DeletesMoreThanOnePage()
        {
            var provider = Azurite.CreateProvider(containerName);
            var dir = NewDir();
            var client = Azurite.Container(containerName);
            await client.CreateIfNotExistsAsync();
            var names = Enumerable.Range(0, 1100).Select(i => $"{dir}/sub{i % 3}/{(i % 7 == 0 ? "deep/" : "")}f{i}.txt").ToList();
            await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 16 },
                async (name, ct) => await client.GetBlobClient(name).UploadAsync(new BinaryData("x"), ct));
            await Write(provider, $"{dir}2/keep.txt", "keep");

            Assert.IsTrue(await provider.DeleteAsync(dir));

            Assert.IsFalse((await provider.GetDirectoryContentsAsync(dir)).Exists);
            var left = 0;
            await foreach (var _ in client.GetBlobsAsync(prefix: $"{dir}/"))
                left++;
            Assert.AreEqual(0, left);
            Assert.IsTrue((await provider.GetFileInfoAsync($"{dir}2/keep.txt")).Exists, "A sibling with the same name prefix was deleted");
            Assert.IsFalse(await provider.DeleteAsync(dir));
        }

        [TestMethod]
        public async Task Delete_FileReturnsTrue_MissingPathReturnsFalse()
        {
            var provider = Azurite.CreateProvider(containerName);
            var dir = NewDir();
            await Write(provider, $"{dir}/f.txt", "x");

            Assert.IsTrue(await provider.DeleteAsync($"{dir}/f.txt"));
            Assert.IsFalse(await provider.DeleteAsync($"{dir}/f.txt"));
            Assert.IsFalse(await provider.DeleteAsync($"{dir}/missing"));
            Assert.IsFalse(await provider.DeleteAsync(NewDir()));
        }

        [TestMethod]
        public async Task Delete_ProviderRootIsRefused()
        {
            var container = Azurite.NewContainerName();
            try
            {
                var provider = Azurite.CreateProvider(container);
                await Write(provider, "keep.txt", "keep");
                var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAsync(""));
                Assert.AreEqual("The provider root cannot be deleted or modified.", ex.Message);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAsync(new string[0]));

                var probe = new RootProbe(Azurite.Options(container));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => probe.DeleteAbsolute());

                var rooted = new RootProbe(Azurite.Options(container, "base/inner"));
                await Write(rooted, "f.txt", "x");
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rooted.DeleteAsync(""));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rooted.DeleteAbsolute("base", "inner"));

                Assert.IsTrue((await provider.GetFileInfoAsync("keep.txt")).Exists);
                Assert.IsTrue((await rooted.GetFileInfoAsync("f.txt")).Exists);
            }
            finally
            {
                await Azurite.DeleteContainer(container);
            }
        }

        [TestMethod]
        public void Protocol_AcceptsOnlyHttpAndHttps()
        {
            foreach (var protocol in new[] { null, "", "http", "https", "HTTPS" })
            {
                var options = Azurite.OfflineOptions("c");
                options.Protocol = protocol;
                Assert.IsNotNull(new WriteableAzureBlobFileProvider(options), protocol);
            }
            foreach (var protocol in new[] { "ftp", "https://", "file", " http" })
            {
                var options = Azurite.OfflineOptions("c");
                options.Protocol = protocol;
                Assert.ThrowsExactly<ArgumentException>(() => new WriteableAzureBlobFileProvider(options), protocol);
            }
        }

        [TestMethod]
        public void Protocol_IsNotChangedOnTheOptions()
        {
            var options = Azurite.OfflineOptions("c");
            options.Protocol = null;
            var provider = new WriteableAzureBlobFileProvider(options);
            Assert.IsNull(provider.Options.Protocol);
        }

        [TestMethod]
        public async Task DownloadUrl_ValidityCappedAndHttpsOnly()
        {
            // The SAS itself, below the base class's checks: the provider still caps at 7 days and signs https only for an
            // https endpoint. Through the public API a validity over 7 days is refused (NotSupportedException).
            var options = Azurite.OfflineOptions("c");
            options.Protocol = "https";
            options.Url = null;
            var provider = new SasProbe(options);

            var query = HttpUtility.ParseQueryString((await provider.Sign("a/b.txt", TimeSpan.FromDays(30))).Url.Query);
            Assert.AreEqual("https", query["spr"]);
            var expires = DateTimeOffset.Parse(query["se"]);
            Assert.IsTrue(expires <= DateTimeOffset.UtcNow.AddDays(7).AddMinutes(1), $"Expiry {expires} exceeds 7 days");
            Assert.IsTrue(expires >= DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-5));

            query = HttpUtility.ParseQueryString((await provider.Sign("a/b.txt", TimeSpan.FromHours(1))).Url.Query);
            Assert.IsTrue(DateTimeOffset.Parse(query["se"]) <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));

            var http = new SasProbe(Azurite.OfflineOptions("c"));
            query = HttpUtility.ParseQueryString((await http.Sign("a/b.txt", TimeSpan.FromHours(1))).Url.Query);
            Assert.IsNull(query["spr"]);

            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetDownloadUrlAsync("a/b.txt", TimeSpan.FromDays(30)));
        }

        [TestMethod]
        public async Task Move_ReleasesLeaseAndRemovesSource()
        {
            var provider = Azurite.CreateProvider(containerName);
            var dir = NewDir();
            await Write(provider, $"{dir}/from.txt", "moved");

            await provider.MoveAsync($"{dir}/from.txt", $"{dir}/to/to.txt");

            Assert.IsFalse((await provider.GetFileInfoAsync($"{dir}/from.txt")).Exists);
            Assert.AreEqual("moved", await Read(await provider.ReadFileAsync($"{dir}/to/to.txt")));
            await Write(provider, $"{dir}/from.txt", "again");
            Assert.AreEqual("again", await Read(await provider.ReadFileAsync($"{dir}/from.txt")));
        }

        [TestMethod]
        public async Task ExistingBlockBlob_OverwriteReplacesIt_AppendFails()
        {
            var provider = Azurite.CreateProvider(containerName);
            var dir = NewDir();
            var client = Azurite.Container(containerName);
            await client.CreateIfNotExistsAsync();
            await client.GetBlobClient($"{dir}/block.txt").UploadAsync(new BinaryData("block"));
            await client.GetBlobClient($"{dir}/block2.txt").UploadAsync(new BinaryData("block"));

            await Write(provider, $"{dir}/block.txt", "new");
            var overwritten = await client.GetBlobClient($"{dir}/block.txt").GetPropertiesAsync();
            Assert.AreEqual(BlobType.Append, overwritten.Value.BlobType);
            Assert.AreEqual("new", await Read(await provider.ReadFileAsync($"{dir}/block.txt")));

            var ex = await Assert.ThrowsExactlyAsync<RequestFailedException>(() =>
                provider.UpdateFileAsync($"{dir}/block2.txt", UpdateFileMode.Append, new MemoryStream(Encoding.UTF8.GetBytes("more"))));
            Assert.AreEqual(BlobErrorCode.InvalidBlobType.ToString(), ex.ErrorCode);
            Assert.AreEqual("block", await Read(await provider.ReadFileAsync($"{dir}/block2.txt")));
        }

        private class SasProbe : WriteableAzureBlobFileProvider
        {
            public SasProbe(AzureFileProviderOptions options) : base(options) { }
            public Task<SignedUrl> Sign(string path, TimeSpan validity) =>
                GetSignedUrlCoreAsync(Resolve(path), new SignedUrlOptions { Validity = validity }, default);
        }

        private class RootProbe : WriteableAzureBlobFileProvider
        {
            public RootProbe(AzureFileProviderOptions options) : base(options) { }
            private ResolvedPath Raw(string[] absolute) => new ResolvedPath(Options.RootDirectorySegments, GetRelativeSegments(absolute));
            public Task<bool> DeleteAbsolute(params string[] absolutePathSegments) => DeleteCoreAsync(Raw(absolutePathSegments), default);
        }
    }
}
