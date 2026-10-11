using System.Runtime.CompilerServices;
using System.Text;

namespace WriteableFileProviders.Common.Tests;

/// <summary>
/// The default move's case-only rename on a store that ignores case: a failure at any step leaves the file under its old
/// name, or, when even that fails, keeps its content at a temporary name that the exception names.
/// </summary>
[TestClass]
public sealed class CaseOnlyRenameTests
{
    private static async Task<FlakyStore> WithFileAsync()
    {
        var store = new FlakyStore();
        await store.UpdateFileAsync("a.txt", new MemoryStream("content"u8.ToArray()));
        return store;
    }

    [TestMethod]
    public async Task Rename_KeepsOnlyTheNewName()
    {
        var store = await WithFileAsync();
        await store.MoveAsync("a.txt", "A.txt");
        CollectionAssert.AreEqual(new[] { "A.txt" }, store.Keys);
        Assert.AreEqual("content", store.Text("A.txt"));
    }

    [TestMethod]
    public async Task Rename_OfALongName_FitsTheStoresNameLimit()
    {
        var store = new FlakyStore { MaxNameLength = 255 };
        var name = new string('a', 246) + ".txt";
        await store.UpdateFileAsync(name, new MemoryStream("content"u8.ToArray()));

        await store.MoveAsync(name, name.ToUpperInvariant());

        CollectionAssert.AreEqual(new[] { name.ToUpperInvariant() }, store.Keys);
    }

    [TestMethod]
    public async Task FailedSourceDelete_LeavesTheSourceAndNoTemporaryCopy()
    {
        var store = await WithFileAsync();
        store.FailDeleteOf = "a.txt";
        await Assert.ThrowsExactlyAsync<IOException>(() => store.MoveAsync("a.txt", "A.txt"));
        CollectionAssert.AreEqual(new[] { "a.txt" }, store.Keys);
    }

    [TestMethod]
    public async Task FailedCopyToTheNewName_PutsTheFileBack()
    {
        var store = await WithFileAsync();
        store.FailWriteTo = "A.txt";
        await Assert.ThrowsExactlyAsync<IOException>(() => store.MoveAsync("a.txt", "A.txt"));
        CollectionAssert.AreEqual(new[] { "a.txt" }, store.Keys);
        Assert.AreEqual("content", store.Text("a.txt"));
    }

    [TestMethod]
    public async Task FailedCopyAndFailedRestore_NameTheTemporaryCopy()
    {
        var store = await WithFileAsync();
        store.FailWriteTo = "A.txt";
        store.FailRestore = true;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => store.MoveAsync("a.txt", "A.txt"));
        var temp = store.Keys.Single();
        StringAssert.StartsWith(temp, ".wfp-rename-");
        StringAssert.Contains(e.Message, temp);
        Assert.AreEqual("content", store.Text(temp));
    }

    [TestMethod]
    public async Task FailedTemporaryCleanup_ReportsTheLeftover()
    {
        var store = await WithFileAsync();
        store.FailDeleteOfTemporary = true;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => store.MoveAsync("a.txt", "A.txt"));
        StringAssert.Contains(e.Message, "could not be removed");
        CollectionAssert.Contains(store.Keys, "A.txt");
    }

    /// <summary>A case-insensitive in-memory store that fails the named step on demand.</summary>
    private sealed class FlakyStore() : WriteableFileProvider<FileProviderOptions>(new FileProviderOptions())
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private bool _restoring;

        public string? FailDeleteOf { get; set; }

        public string? FailWriteTo { get; set; }

        public bool FailRestore { get; set; }

        public bool FailDeleteOfTemporary { get; set; }

        /// <summary>The longest name the store takes, as Box and SharePoint limit names to 255 characters.</summary>
        public int? MaxNameLength { get; set; }

        public string[] Keys => [.. _files.Keys.Order(StringComparer.Ordinal)];

        public string Text(string key) => Encoding.UTF8.GetString(_files[key]);

        protected override bool IsCaseInsensitive => true;

        protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
            Task.FromResult<IWriteableFileInfo>(_files.TryGetValue(path.Path.ToString(), out var bytes)
                ? new WriteableFileInfo(this, path.Path, true, false, bytes.Length, DateTimeOffset.UtcNow, etag: bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : WriteableFileInfo.Missing(this, path.Path));

        protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
            _files.TryGetValue(path.Path.ToString(), out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new KeyNotFoundException();

        protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
        {
            var key = path.Path.ToString();
            if (path.Name.Length > MaxNameLength)
                throw new ArgumentException($"The name '{path.Name}' is too long.", nameof(path));
            // The rename's copy back to the old name happens after the copy to the new name failed.
            if (FailRestore && _restoring)
                throw new IOException("restore failure");
            if (string.Equals(key, FailWriteTo, StringComparison.Ordinal))
            {
                _restoring = true;
                throw new IOException("write failure");
            }
            using var buffer = new MemoryStream();
            await contents.CopyToAsync(buffer, cancellationToken);
            _files.Remove(key);
            _files[key] = buffer.ToArray();
        }

        protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
        {
            var key = path.Path.ToString();
            if (string.Equals(key, FailDeleteOf, StringComparison.Ordinal))
                throw new IOException("delete failure");
            if (FailDeleteOfTemporary && key.Contains(".wfp-", StringComparison.Ordinal))
                throw new IOException("temporary delete failure");
            return Task.FromResult(_files.Remove(key));
        }

        protected override bool IsNotFound(Exception exception) => exception is KeyNotFoundException;
    }
}
