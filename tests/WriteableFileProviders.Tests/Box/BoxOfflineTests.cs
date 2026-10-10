using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Box.V2;
using Box.V2.Auth;
using Box.V2.Config;
using Box.V2.Converter;
using Box.V2.Exceptions;
using Box.V2.Request;
using Box.V2.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box;

namespace WriteableFileProviders.Tests
{
    // The Box provider over a fake Box API: each test drives the provider's own code without a Box account.
    [TestClass]
    public class BoxOfflineTests
    {
        [TestMethod]
        public async Task GetFileInfo_FileFolderAndMissing()
        {
            var (provider, box) = createProvider();
            var docs = box.AddFolder("0", "docs");
            box.AddFile(docs, "a.txt", "hello");

            var file = await provider.GetFileInfoAsync("docs/a.txt");
            Assert.IsTrue(file.Exists);
            Assert.IsFalse(file.IsDirectory);
            Assert.AreEqual(5, file.Length);
            Assert.AreEqual("a.txt", file.Name);
            Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), file.LastModified);

            // Box still says Exists=false for a folder (F20, fixed in S10); IsDirectory is already right.
            var folder = await provider.GetFileInfoAsync("DOCS");
            Assert.IsTrue(folder.IsDirectory);

            Assert.IsFalse((await provider.GetFileInfoAsync("docs/missing.txt")).Exists);
            Assert.IsFalse((await provider.GetFileInfoAsync("docs/a.txt/below")).Exists);
        }

        [TestMethod]
        public async Task GetFileInfo_WithoutSizeInTheListing_AsksForTheFile()
        {
            var (provider, box) = createProvider();
            var id = box.AddFile("0", "a.txt", "hello");
            box.OmitSizeInListings = true;

            var info = await provider.GetFileInfoAsync("a.txt");
            Assert.AreEqual(5, info.Length);
            CollectionAssert.Contains(box.Requests, "GET files/" + id);
        }

        [TestMethod]
        public async Task Listing_PagesThroughTheFolder()
        {
            var (provider, box) = createProvider();
            var big = box.AddFolder("0", "big");
            for (var i = 0; i < 502; i++)
                box.AddFile(big, $"f{i:D3}.txt", "x");
            box.AddFolder(big, "sub");

            var contents = await provider.GetDirectoryContentsAsync("big");
            Assert.IsTrue(contents.Exists);
            var entries = contents.ToList();
            Assert.AreEqual(503, entries.Count);
            Assert.IsTrue(entries.Single(e => e.Name == "sub").IsDirectory);
            Assert.AreEqual(1, entries.Single(e => e.Name == "f000.txt").Length);
            Assert.AreEqual("big/f501.txt", ((IWriteableFileInfo)entries.Single(e => e.Name == "f501.txt")).Path.ToString());

            Assert.IsFalse((await provider.GetDirectoryContentsAsync("missing")).Exists);
        }

        [TestMethod]
        public async Task ReadFile_DownloadsTheContent()
        {
            var (provider, box) = createProvider();
            box.AddFile("0", "a.txt", "hello");

            using var stream = await provider.ReadFileAsync("a.txt");
            using var reader = new StreamReader(stream);
            Assert.AreEqual("hello", await reader.ReadToEndAsync());

            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("missing.txt"));
        }

        [TestMethod]
        public async Task UpdateFile_NewFile_CreatesTheParentFoldersAndUploads()
        {
            var (provider, box) = createProvider();

            await provider.UpdateFileAsync("x/y/z.txt", text("new"), UpdateFileMode.Overwrite);

            Assert.AreEqual("new", box.ContentOf("x/y/z.txt"));
            Assert.AreEqual(2, box.Requests.Count(r => r == "POST folders"));
        }

        [TestMethod]
        public async Task UpdateFile_Overwrite_UploadsANewVersion()
        {
            var (provider, box) = createProvider();
            var id = box.AddFile("0", "a.txt", "old");

            await provider.UpdateFileAsync("a.txt", text("new"), UpdateFileMode.Overwrite);

            Assert.AreEqual("new", box.ContentOf("a.txt"));
            CollectionAssert.Contains(box.Requests, "POST files/" + id + "/content");
        }

        [TestMethod]
        public async Task UpdateFile_Append_CombinesTheContents()
        {
            var (provider, box) = createProvider();
            box.AddFile("0", "a.txt", "one,");

            await provider.UpdateFileAsync("a.txt", text("two"), UpdateFileMode.Append);

            Assert.AreEqual("one,two", box.ContentOf("a.txt"));
        }

        [TestMethod]
        public async Task UpdateFile_UnseekableContent_IsSpooledFirst()
        {
            var (provider, box) = createProvider();
            box.AddFile("0", "old.txt", "old");

            await provider.UpdateFileAsync("new.txt", new UnseekableStream("fresh"), UpdateFileMode.Overwrite);
            await provider.UpdateFileAsync("old.txt", new UnseekableStream("newer"), UpdateFileMode.Overwrite);

            Assert.AreEqual("fresh", box.ContentOf("new.txt"));
            Assert.AreEqual("newer", box.ContentOf("old.txt"));
        }

        [TestMethod]
        public async Task UpdateFile_UnderAFile_IsRefused()
        {
            var (provider, box) = createProvider();
            box.AddFile("0", "a.txt", "x");

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                provider.UpdateFileAsync("a.txt/b.txt", text("y"), UpdateFileMode.Overwrite));
        }

        [TestMethod]
        public async Task Delete_FileFolderAndMissing()
        {
            var (provider, box) = createProvider();
            var docs = box.AddFolder("0", "docs");
            var fileId = box.AddFile(docs, "a.txt", "x");

            Assert.IsTrue(await provider.DeleteAsync("docs/a.txt"));
            CollectionAssert.Contains(box.Requests, "DELETE files/" + fileId);
            Assert.IsTrue(await provider.DeleteAsync("docs"));
            CollectionAssert.Contains(box.Requests, "DELETE folders/" + docs + "?recursive=true");
            Assert.IsFalse(await provider.DeleteAsync("docs"));
            Assert.IsFalse((await provider.GetFileInfoAsync("docs")).Exists);
        }

        [TestMethod]
        public async Task Move_FileAndCaseOnlyRename()
        {
            var (provider, box) = createProvider();
            box.AddFolder("0", "target");
            var id = box.AddFile("0", "a.txt", "x");

            await provider.MoveAsync("a.txt", "A.txt");
            Assert.AreEqual("A.txt", box.NameOf(id));

            await provider.MoveAsync("A.txt", "target/b.txt");
            Assert.AreEqual("x", box.ContentOf("target/b.txt"));
            Assert.IsFalse((await provider.GetFileInfoAsync("A.txt")).Exists);
        }

        [TestMethod]
        public async Task Move_ToAMissingFolder_IsRefused()
        {
            var (provider, box) = createProvider();
            box.AddFile("0", "a.txt", "x");

            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => provider.MoveAsync("a.txt", "missing/b.txt"));
            Assert.AreEqual("x", box.ContentOf("a.txt"));
        }

        [TestMethod]
        public async Task SignedUrl_IsBoxsDownloadUrl()
        {
            var (provider, box) = createProvider();
            var id = box.AddFile("0", "a.txt", "x");

            var url = await provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(10) });

            Assert.AreEqual(new Uri(FakeBox.DownloadHost + id), url.Url);
            Assert.AreEqual(SignedUrlKind.PreAuthenticated, url.Kind);
            Assert.IsNull(url.ExpiresOn);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                provider.GetSignedUrlAsync("a.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(10), Permission = SignedUrlAccess.Write }));
        }

        [TestMethod]
        public async Task RootFolderId_IsTheProviderRoot()
        {
            var box = new FakeBox();
            var root = box.AddFolder("0", "tenant");
            box.AddFile(root, "a.txt", "inside");
            box.AddFile("0", "a.txt", "outside");
            var provider = createProvider(box, root);

            using var reader = new StreamReader(await provider.ReadFileAsync("a.txt"));
            Assert.AreEqual("inside", await reader.ReadToEndAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAsync(""));
        }

        [TestMethod]
        public async Task ApiErrors_AreWrappedWithThePath()
        {
            var (provider, box) = createProvider();
            box.AddFile("0", "a.txt", "x");
            box.FailFolderListings = true;

            var ex = await Assert.ThrowsAsync<BoxException>(() => provider.GetFileInfoAsync("a.txt"));
            StringAssert.Contains(ex.Message, "a.txt");
        }

        private static (WriteableBoxFileProvider provider, FakeBox box) createProvider()
        {
            var box = new FakeBox();
            return (createProvider(box, null), box);
        }

        private static WriteableBoxFileProvider createProvider(FakeBox box, string rootFolderId)
        {
            var config = new BoxConfigBuilder("client", "secret").Build();
            var converter = new BoxJsonConverter();
            var service = new BoxService(box);
            var auth = new AuthRepository(config, service, converter, new OAuthSession("offline", "refresh", 3600, "bearer"));
            var client = new BoxClient(config, converter, box, service, auth, null, null);
            var options = new BoxFileProviderOptions("client", "secret", "enterprise", "key", "pem", null, rootFolderId);
            return new WriteableBoxFileProvider(options, client);
        }

        private static MemoryStream text(string value) => new MemoryStream(Encoding.UTF8.GetBytes(value));

        private sealed class UnseekableStream : MemoryStream
        {
            public UnseekableStream(string text) : base(Encoding.UTF8.GetBytes(text)) { }
            public override bool CanSeek => false;
        }

        // A Box account in memory: the folder and file calls the provider makes, answered as the Box API answers them.
        private sealed class FakeBox : IRequestHandler
        {
            public const string DownloadHost = "https://dl.boxcloud.test/d/";

            private sealed class Item
            {
                public string Id;
                public string Type;
                public string Name;
                public string ParentId;
                public byte[] Content = Array.Empty<byte>();
            }

            private readonly Dictionary<string, Item> items = new Dictionary<string, Item>();
            private int nextId = 100;

            public FakeBox() => items["0"] = new Item { Id = "0", Type = "folder", Name = "All Files" };

            public List<string> Requests { get; } = new List<string>();
            public bool OmitSizeInListings { get; set; }
            public bool FailFolderListings { get; set; }

            public string AddFolder(string parentId, string name) => add("folder", parentId, name, null);

            public string AddFile(string parentId, string name, string text) => add("file", parentId, name, Encoding.UTF8.GetBytes(text));

            public string NameOf(string id) => items[id].Name;

            public string ContentOf(string path)
            {
                var current = items["0"];
                foreach (var name in path.Split('/'))
                    current = items.Values.Single(i => i.ParentId == current.Id && i.Name == name);
                return Encoding.UTF8.GetString(current.Content);
            }

            public Task<IBoxResponse<T>> ExecuteAsync<T>(IBoxRequest request) where T : class => Task.FromResult(handle<T>(request));

            public Task<IBoxResponse<T>> ExecuteAsyncWithoutRetry<T>(IBoxRequest request) where T : class => ExecuteAsync<T>(request);

            private string add(string type, string parentId, string name, byte[] content)
            {
                var id = (nextId++).ToString();
                items[id] = new Item { Id = id, Type = type, Name = name, ParentId = parentId, Content = content ?? Array.Empty<byte>() };
                return id;
            }

            private IBoxResponse<T> handle<T>(IBoxRequest request) where T : class
            {
                var uri = request.AbsoluteUri;
                if (uri.ToString().StartsWith(DownloadHost, StringComparison.Ordinal))
                {
                    Requests.Add("DOWNLOAD");
                    var bytes = items[uri.Segments.Last()].Content;
                    return new BoxResponse<T> { Status = ResponseStatus.Success, StatusCode = HttpStatusCode.OK, ResponseObject = (T)(object)new MemoryStream(bytes) };
                }

                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var route = segments.Skip(Array.IndexOf(segments, "2.0") + 1).ToArray();
                var recursive = uri.Query.Contains("recursive=true") ? "?recursive=true" : "";
                Requests.Add(request.Method.ToString().ToUpperInvariant() + " " + string.Join("/", route) + recursive);

                switch (request.Method, route)
                {
                    case (RequestMethod.Get, [ "folders", var id, "items" ]):
                        if (FailFolderListings)
                            return error<T>(HttpStatusCode.InternalServerError, "internal_server_error");
                        var query = parseQuery(uri.Query);
                        var children = items.Values.Where(i => i.ParentId == id).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
                        var offset = int.Parse(query["offset"]);
                        var limit = int.Parse(query["limit"]);
                        var page = new JsonArray(children.Skip(offset).Take(limit).Select(i => (JsonNode)json(i, OmitSizeInListings)).ToArray());
                        return ok<T>(new JsonObject { ["total_count"] = children.Count, ["offset"] = offset, ["limit"] = limit, ["entries"] = page });

                    case (RequestMethod.Get, [ "files", var id ]):
                        return ok<T>(json(items[id], false));

                    case (RequestMethod.Get, [ "files", var id, "content" ]) when typeof(T) == typeof(Stream):
                        return new BoxResponse<T> { Status = ResponseStatus.Success, StatusCode = HttpStatusCode.OK, ResponseObject = (T)(object)new MemoryStream(items[id].Content) };

                    case (RequestMethod.Get, [ "files", var id, "content" ]):
                        var found = new HttpResponseMessage(HttpStatusCode.Found);
                        found.Headers.Location = new Uri(DownloadHost + id);
                        return new BoxResponse<T> { Status = ResponseStatus.Success, StatusCode = HttpStatusCode.Found, Headers = found.Headers };

                    case (RequestMethod.Post, [ "folders" ]):
                        var folder = JsonNode.Parse(request.Payload);
                        return ok<T>(json(items[add("folder", (string)folder["parent"]["id"], (string)folder["name"], null)], false));

                    case (RequestMethod.Post, [ "files", "content" ]):
                        var (attributes, content) = multipart(request);
                        var created = items[add("file", (string)attributes["parent"]["id"], (string)attributes["name"], content)];
                        return ok<T>(new JsonObject { ["total_count"] = 1, ["entries"] = new JsonArray(json(created, false)) });

                    case (RequestMethod.Post, [ "files", var id, "content" ]):
                        items[id].Content = multipart(request).content;
                        return ok<T>(new JsonObject { ["total_count"] = 1, ["entries"] = new JsonArray(json(items[id], false)) });

                    case (RequestMethod.Put, [ "files" or "folders", var id ]):
                        var update = JsonNode.Parse(request.Payload);
                        items[id].Name = (string)update["name"] ?? items[id].Name;
                        items[id].ParentId = (string)update["parent"]?["id"] ?? items[id].ParentId;
                        return ok<T>(json(items[id], false));

                    case (RequestMethod.Delete, [ "files" or "folders", var id ]):
                        foreach (var gone in items.Values.Where(i => isUnder(i, id)).ToList())
                            items.Remove(gone.Id);
                        return new BoxResponse<T> { Status = ResponseStatus.Success, StatusCode = HttpStatusCode.NoContent };

                    default:
                        return error<T>(HttpStatusCode.NotFound, "not_found");
                }
            }

            private bool isUnder(Item item, string id)
            {
                for (var current = item; current != null; current = current.ParentId == null ? null : items.GetValueOrDefault(current.ParentId))
                    if (current.Id == id)
                        return true;
                return false;
            }

            private static JsonObject json(Item item, bool omitSize)
            {
                var node = new JsonObject
                {
                    ["type"] = item.Type,
                    ["id"] = item.Id,
                    ["name"] = item.Name,
                    ["modified_at"] = "2026-01-02T03:04:05Z",
                };
                if (item.Type == "file" && !omitSize)
                    node["size"] = item.Content.Length;
                return node;
            }

            private static (JsonNode attributes, byte[] content) multipart(IBoxRequest request)
            {
                var parts = ((BoxMultiPartRequest)request).Parts;
                var attributes = parts.OfType<BoxStringFormPart>().Single(p => p.Name == "attributes").Value;
                var file = parts.OfType<BoxFileFormPart>().Single().Value;
                using var copy = new MemoryStream();
                file.CopyTo(copy);
                return (JsonNode.Parse(attributes), copy.ToArray());
            }

            private static Dictionary<string, string> parseQuery(string query) =>
                query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

            private static IBoxResponse<T> ok<T>(JsonNode body) where T : class =>
                new BoxResponse<T> { Status = ResponseStatus.Success, StatusCode = HttpStatusCode.OK, ContentString = body.ToJsonString() };

            private static IBoxResponse<T> error<T>(HttpStatusCode status, string code) where T : class =>
                new BoxResponse<T>
                {
                    Status = ResponseStatus.Error,
                    StatusCode = status,
                    ContentString = new JsonObject { ["type"] = "error", ["status"] = (int)status, ["code"] = code, ["message"] = code }.ToJsonString(),
                };
        }
    }
}
