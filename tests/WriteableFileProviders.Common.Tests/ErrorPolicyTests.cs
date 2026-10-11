using System.Security;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

/// <summary>
/// What a public member of the base class lets out when the store fails: only the contract's types, with the store's
/// exception inside. A provider that throws whatever a test gives it, from every <c>...CoreAsync</c> member, from the
/// streams it returns and from its listings.
/// </summary>
[TestClass]
public sealed class ErrorPolicyTests
{
    private static readonly byte[] _data = TestStreams.Bytes(100);

    // Every public member, each named for the messages, with what its message starts with, and whether it takes a token.
    private static IEnumerable<(string Name, string Names, bool TakesAToken, Func<FaultyProvider, CancellationToken, Task> Call)> Members()
    {
        yield return ("GetFileInfoAsync", "Getting 'f.txt'", true, (p, t) => p.GetFileInfoAsync("f.txt", t));
        yield return ("GetFileInfo", "Getting 'f.txt'", false, (p, _) => Task.Run(() => p.GetFileInfo("f.txt")));
        yield return ("GetDirectoryContentsAsync", "Listing 'dir'", true, (p, t) => p.GetDirectoryContentsAsync("dir", t));
        yield return ("GetDirectoryContents", "Listing the root folder", false, (p, _) => Task.Run(() => p.GetDirectoryContents("")));
        yield return ("EnumerateAsync", "Listing 'dir'", true, async (p, t) => await p.EnumerateAsync("dir", cancellationToken: t).ToListAsync(CancellationToken.None));
        yield return ("EnumerateAsync recursive", "Listing 'dir'", true, async (p, t) => await p.EnumerateAsync("dir", recursive: true, t).ToListAsync(CancellationToken.None));
        yield return ("ReadFileAsync", "Reading 'f.txt'", true, (p, t) => p.ReadFileAsync("f.txt", t));
        yield return ("ReadRangeAsync", "Reading 'f.txt'", true, (p, t) => p.ReadRangeAsync("f.txt", 1, 2, cancellationToken: t));
        yield return ("OpenReadSeekableAsync", "Reading 'f.txt'", true, (p, t) => p.OpenReadSeekableAsync("f.txt", cancellationToken: t));
        yield return ("UpdateFileAsync", "Writing 'f.txt'", true, (p, t) => p.UpdateFileAsync("f.txt", TestStreams.Text("x"), cancellationToken: t));
        yield return ("UpdateFileAsync append", "Writing 'f.txt'", true, (p, t) => p.UpdateFileAsync("f.txt", TestStreams.Text("x"), UpdateFileOptions.Append, t));
        yield return ("DeleteAsync", "Deleting 'f.txt'", true, (p, t) => p.DeleteAsync("f.txt", t));
        yield return ("MoveAsync", "Moving 'f.txt' to 'g.txt'", true, (p, t) => p.MoveAsync("f.txt", "g.txt", t));
        yield return ("CopyAsync", "Copying 'f.txt' to 'g.txt'", true, (p, t) => p.CopyAsync("f.txt", "g.txt", t));
        yield return ("CreateLocalFileStreamAsync", "Opening a local copy of 'f.txt'", true, (p, t) => p.CreateLocalFileStreamAsync("f.txt", readOnly: true, t));
        yield return ("GetSignedUrlAsync", "Signing a URL for 'f.txt'", true, (p, t) => p.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) }, t));
        yield return ("ReadVersionAsync", "Reading 'f.txt'", true, (p, t) => p.ReadVersionAsync("f.txt", t));
    }

#pragma warning disable CS0618 // the [Obsolete] 2026 forms are what this test pins
    // The 2026 forms of WriteableFileProviderCompatibilityExtensions that reach the store: synchronous ones run on the
    // thread pool, so each is awaited as a task.
    private static IEnumerable<(string Name, Func<FaultyProvider, Task> Call)> ObsoleteForms()
    {
        static Microsoft.Extensions.FileProviders.IFileInfo Entry(FaultyProvider p) =>
            new WriteableFileInfo(p, "f.txt", exists: true, isDirectory: false, 1, DateTimeOffset.UtcNow);
        var day = TimeSpan.FromDays(1);
        yield return ("GetFileInfo(path)", p => Task.Run(() => p.GetFileInfo(new ProviderPath("f.txt"))));
        yield return ("GetDirectoryContents(path)", p => Task.Run(() => p.GetDirectoryContents(new ProviderPath("dir"))));
        yield return ("ReadFile(path)", p => Task.Run(() => p.ReadFile("f.txt")));
        yield return ("UpdateFile(path)", p => Task.Run(() => p.UpdateFile("f.txt", TestStreams.Text("x"))));
        yield return ("UpdateFile(path, mode)", p => Task.Run(() => p.UpdateFile("f.txt", TestStreams.Text("x"), UpdateFileMode.Append)));
        yield return ("UpdateFileAsync(path, mode)", p => p.UpdateFileAsync("f.txt", TestStreams.Text("x"), UpdateFileMode.Append));
        yield return ("Delete(path)", p => Task.Run(() => p.Delete("f.txt")));
        yield return ("Move(path)", p => Task.Run(() => p.Move("f.txt", "g.txt")));
        yield return ("CreateLocalFileStream(path)", p => Task.Run(() => p.CreateLocalFileStream("f.txt", readOnly: true)));
        yield return ("GetDownloadUrlAsync(path)", p => p.GetDownloadUrlAsync("f.txt", day));
        yield return ("GetDownloadUrl(path)", p => Task.Run(() => p.GetDownloadUrl("f.txt", day)));
        yield return ("ReadFileAsync(entry)", p => p.ReadFileAsync(Entry(p)));
        yield return ("ReadFile(entry)", p => Task.Run(() => p.ReadFile(Entry(p))));
        yield return ("UpdateFileAsync(entry)", p => p.UpdateFileAsync(Entry(p), TestStreams.Text("x")));
        yield return ("UpdateFile(entry)", p => Task.Run(() => p.UpdateFile(Entry(p), TestStreams.Text("x"))));
        yield return ("DeleteAsync(entry)", p => p.DeleteAsync(Entry(p)));
        yield return ("Delete(entry)", p => Task.Run(() => p.Delete(Entry(p))));
        yield return ("MoveAsync(entry)", p => p.MoveAsync(Entry(p), "g.txt"));
        yield return ("Move(entry)", p => Task.Run(() => p.Move(Entry(p), "g.txt")));
        yield return ("CreateLocalFileStreamAsync(entry)", p => p.CreateLocalFileStreamAsync(Entry(p), readOnly: true));
        yield return ("CreateLocalFileStream(entry)", p => Task.Run(() => p.CreateLocalFileStream(Entry(p), readOnly: true)));
        yield return ("GetDownloadUrlAsync(entry)", p => p.GetDownloadUrlAsync(Entry(p), day));
        yield return ("GetDownloadUrl(entry)", p => Task.Run(() => p.GetDownloadUrl(Entry(p), day)));
    }
#pragma warning restore CS0618

    [TestMethod]
    public async Task TheObsolete2026Forms_LetOutTheContractsExceptions_AsTheAsyncMembersDo()
    {
        foreach (var (name, call) in ObsoleteForms())
        {
            var failure = new StoreFailureException("the SDK's own exception");
            using (var provider = new FaultyProvider { Fail = _ => failure })
                Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(() => call(provider), name)).InnerException, name);

            var missing = new KeyNotFoundException("The store has no such key.");
            using (var provider = new FaultyProvider { Fail = _ => missing })
                Assert.AreSame(missing, (await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => call(provider), name)).InnerException, name);

            var refused = new SecurityException("403 Forbidden");
            using (var provider = new FaultyProvider { Fail = _ => refused })
                Assert.AreSame(refused, (await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => call(provider), name)).InnerException, name);
        }
    }

    [TestMethod]
    public async Task EveryMember_TurnsAnExceptionOutsideTheContractIntoIOException_WithItInside()
    {
        foreach (var (name, names, _, call) in Members())
        {
            var failure = new StoreFailureException("the SDK's own exception");
            using var provider = new FaultyProvider { Fail = _ => failure };
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => call(provider, CancellationToken.None), name);
            Assert.AreSame(failure, e.InnerException, name);
            StringAssert.StartsWith(e.Message, names, name);
            StringAssert.Contains(e.Message, nameof(StoreFailureException), name);
            Assert.IsFalse(e.Message.Contains("the SDK's own exception", StringComparison.Ordinal), $"{name}: the store's message, which may hold a credential, stays inside");
        }
    }

    [TestMethod]
    public void Watch_TurnsAnExceptionOutsideTheContractIntoIOException()
    {
        var failure = new StoreFailureException("the watcher failed");
        using var provider = new FaultyProvider { Fail = _ => failure };
        var e = Assert.ThrowsExactly<IOException>(() => provider.Watch("**/*"));
        Assert.AreSame(failure, e.InnerException);
        StringAssert.StartsWith(e.Message, "Watching for changes");
        provider.Fail = _ => new NotSupportedException("No watching here.");
        Assert.ThrowsExactly<NotSupportedException>(() => provider.Watch("**/*"));
        provider.Fail = null;
        Assert.IsNotNull(provider.Watch("**/*"));
    }

    [TestMethod]
    [DataRow(typeof(ArgumentException))]
    [DataRow(typeof(ArgumentOutOfRangeException))]
    [DataRow(typeof(FileNotFoundException))]
    [DataRow(typeof(FileExistsException))]
    [DataRow(typeof(PreconditionFailedException))]
    [DataRow(typeof(NotSupportedException))]
    [DataRow(typeof(InvalidOperationException))]
    [DataRow(typeof(ObjectDisposedException))]
    [DataRow(typeof(UnauthorizedAccessException))]
    [DataRow(typeof(IOException))]
    [DataRow(typeof(EndOfStreamException))]
    [DataRow(typeof(OutOfMemoryException))]
    public async Task TheContractsTypes_PassThroughUnchanged(Type type)
    {
        foreach (var (name, _, _, call) in Members())
        {
            var failure = (Exception)Activator.CreateInstance(type, "The provider's own.")!;
            using var provider = new FaultyProvider { Fail = _ => failure };
            Assert.AreSame(failure, await Assert.ThrowsAsync<Exception>(() => call(provider, CancellationToken.None), name), name);
        }
    }

    [TestMethod]
    public async Task WhatIsNotFoundRecognises_IsFileNotFound_ForThePath()
    {
        foreach (var (name, _, _, call) in Members())
        {
            var missing = new KeyNotFoundException("The store has no such key.");
            using var provider = new FaultyProvider { Fail = _ => missing };
            var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => call(provider, CancellationToken.None), name);
            Assert.AreSame(missing, e.InnerException, name);
            Assert.IsTrue(e.FileName is "f.txt" or "dir" or "", $"{name}: {e.FileName}");
        }
    }

    [TestMethod]
    public async Task WhatIsAccessDeniedRecognises_IsUnauthorizedAccess()
    {
        foreach (var (name, _, _, call) in Members())
        {
            var refused = new SecurityException("403 Forbidden");
            using var provider = new FaultyProvider { Fail = _ => refused };
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => call(provider, CancellationToken.None), name);
            Assert.AreSame(refused, e.InnerException, name);
            StringAssert.Contains(e.Message, "refused", name);
        }
    }

    [TestMethod]
    public async Task APredicateThatFails_CountsAsNo()
    {
        var failure = new KeyNotFoundException("missing");
        using var provider = new FaultyProvider { Fail = _ => failure, PredicatesFail = true };
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.ReadFileAsync("f.txt"));
        Assert.AreSame(failure, e.InnerException);
    }

    [TestMethod]
    public async Task TheCallersCancellation_IsOperationCanceled_AndATimeoutIsIOException()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        foreach (var (name, _, takesAToken, call) in Members())
        {
            // Nobody cancelled: the store or its client gave up, as an HTTP client does at its timeout.
            var timeout = new TaskCanceledException("A task was canceled.", new TimeoutException("The request timed out."));
            using (var provider = new FaultyProvider { Fail = _ => timeout })
            {
                var e = await Assert.ThrowsExactlyAsync<IOException>(() => call(provider, CancellationToken.None), name);
                Assert.AreSame(timeout, e.InnerException, name);
                StringAssert.Contains(e.Message, "did not complete", name);
            }
            if (!takesAToken)
                continue;

            // The caller cancelled: the cancellation passes through as it is.
            var cancellation = new OperationCanceledException(cancelled.Token);
            using (var provider = new FaultyProvider { Fail = _ => cancellation })
                Assert.AreSame(cancellation, await Assert.ThrowsAsync<OperationCanceledException>(() => call(provider, cancelled.Token), name));

            // The caller cancelled, and the store failed another way because of it: a cancellation, the failure inside.
            var aborted = new StoreFailureException("The connection was aborted.");
            using (var provider = new FaultyProvider { Fail = _ => aborted })
            {
                var e = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => call(provider, cancelled.Token), name);
                Assert.AreSame(aborted, e.InnerException, name);
                Assert.AreEqual(cancelled.Token, e.CancellationToken, name);
            }
        }
    }

    [TestMethod]
    public async Task ReadStreams_MapTheFailuresOfTheirReads()
    {
        foreach (var open in new Func<FaultyProvider, Task<Stream>>[]
        {
            p => p.ReadFileAsync("f.txt"),
            p => p.ReadRangeAsync("f.txt", 0, null),
            p => p.ReadVersionAsync("f.txt", CancellationToken.None),
        })
        {
            var failure = new StoreFailureException("The connection reset.");
            using var provider = new FaultyProvider { StreamFailure = failure, FailAfter = 10 };
            await using var stream = await open(provider);
            var buffer = new byte[64];
            Assert.AreEqual(10, await stream.ReadAsync(buffer));
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => stream.ReadAsync(buffer).AsTask());
            Assert.AreSame(failure, e.InnerException);
            StringAssert.StartsWith(e.Message, "Reading 'f.txt'");
#pragma warning disable CA1835 // The array overload is one of those under test.
            Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(() => stream.ReadAsync(buffer, 0, 64))).InnerException);
#pragma warning restore CA1835
            Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.Read(buffer, 0, 64)).InnerException);
            Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.Read(buffer.AsSpan())).InnerException);
            Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.ReadByte()).InnerException);
            Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(() => stream.CopyToAsync(Stream.Null))).InnerException);
        }
    }

    [TestMethod]
    public async Task ReadStreams_MapNotFound_AndLetTheContractAndTheCallersCancellationThrough()
    {
        using (var provider = new FaultyProvider { StreamFailure = new KeyNotFoundException("deleted meanwhile"), FailAfter = 0 })
        {
            await using var stream = await provider.ReadFileAsync("f.txt");
            Assert.AreEqual("f.txt", (await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => stream.ReadAsync(new byte[4]).AsTask())).FileName);
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancellation = new OperationCanceledException(cancelled.Token);
        using (var provider = new FaultyProvider { StreamFailure = cancellation, FailAfter = 0 })
        {
            await using var stream = await provider.ReadFileAsync("f.txt");
            Assert.AreSame(cancellation, await Assert.ThrowsAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[4], cancelled.Token).AsTask()));
            // A synchronous read has no token: a cancellation there is the store giving up.
            Assert.AreSame(cancellation, Assert.ThrowsExactly<IOException>(() => stream.Read(new byte[4], 0, 4)).InnerException);
        }

        var ended = new EndOfStreamException("The file is shorter than it said.");
        using (var provider = new FaultyProvider { StreamFailure = ended, FailAfter = 0 })
        {
            await using var stream = await provider.ReadFileAsync("f.txt");
            Assert.AreSame(ended, await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => stream.ReadAsync(new byte[4]).AsTask()));
        }
    }

    [TestMethod]
    public async Task ReadStreams_KeepWhatTheProvidersStreamCanDo()
    {
        using var provider = new FaultyProvider();
        await using var seekable = await provider.OpenReadSeekableAsync("f.txt");
        Assert.IsTrue(seekable.CanRead);
        Assert.IsTrue(seekable.CanSeek);
        Assert.IsFalse(seekable.CanWrite);
        Assert.AreEqual(_data.Length, seekable.Length);
        Assert.AreEqual(40, seekable.Seek(40, SeekOrigin.Begin));
        Assert.AreEqual(40, seekable.Position);
        Assert.AreEqual(_data[40], (byte)seekable.ReadByte());
        seekable.Position = 99;
        Assert.AreEqual(_data[99], (byte)seekable.ReadByte());
        Assert.AreEqual(-1, seekable.ReadByte());
        Assert.ThrowsExactly<NotSupportedException>(() => seekable.Write([1], 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => seekable.SetLength(1));
        seekable.Flush();
        await seekable.FlushAsync();
        Assert.IsFalse(seekable.CanTimeout);

        await using var forward = await provider.ReadFileAsync("f.txt");
        Assert.IsFalse(forward.CanSeek);
        Assert.ThrowsExactly<NotSupportedException>(() => forward.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => forward.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => forward.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => forward.Seek(1, SeekOrigin.Begin));
        Assert.ThrowsExactly<InvalidOperationException>(() => forward.ReadTimeout);
        Assert.ThrowsExactly<InvalidOperationException>(() => forward.ReadTimeout = 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => forward.WriteTimeout);
        Assert.ThrowsExactly<InvalidOperationException>(() => forward.WriteTimeout = 1);
    }

    [TestMethod]
    public async Task ReadStreams_MapTheFailuresOfTheirPositionAndDisposal()
    {
        var failure = new StoreFailureException("The handle went away.");
        using var provider = new FaultyProvider { StreamFailure = failure, FailAfter = int.MaxValue, FailEverything = true };
        var stream = await provider.OpenReadSeekableAsync("f.txt");
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.Length).InnerException);
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.Position).InnerException);
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.Position = 1).InnerException);
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => stream.Seek(1, SeekOrigin.Begin)).InnerException);
        Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(async () => await stream.DisposeAsync())).InnerException);
        await stream.DisposeAsync();
        var synchronous = await provider.OpenReadSeekableAsync("f.txt");
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(synchronous.Dispose).InnerException);
        synchronous.Dispose();
    }

    [TestMethod]
    public async Task Listings_MapAFailureOfTheirPagesAndOfTheirDisposal()
    {
        var failure = new StoreFailureException("The next page failed.");
        using (var provider = new FaultyProvider { ListFailure = failure })
        {
            var entries = new List<IWriteableFileInfo>();
            var e = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            {
                await foreach (var entry in provider.EnumerateAsync("dir"))
                    entries.Add(entry);
            });
            Assert.AreSame(failure, e.InnerException);
            Assert.HasCount(1, entries, "What came before the failure was listed");
            Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetDirectoryContentsAsync("dir"))).InnerException);
        }

        using (var provider = new FaultyProvider { ListDisposalFailure = failure })
        {
            var e = await Assert.ThrowsExactlyAsync<IOException>(async () => await provider.EnumerateAsync("dir").ToListAsync());
            Assert.AreSame(failure, e.InnerException);
        }

        using (var provider = new FaultyProvider { ListFailsAtOnce = failure })
            Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(async () => await provider.EnumerateAsync("dir").ToListAsync())).InnerException);

        // A caller that stops early disposes the listing, as a foreach that breaks does.
        using (var provider = new FaultyProvider())
            Assert.IsNotNull(await provider.EnumerateAsync("dir").FirstAsync());
    }

    [TestMethod]
    public async Task Listings_LetTheCallersCancellationThrough()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var provider = new FaultyProvider { ListFailure = new OperationCanceledException(cancelled.Token) };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.EnumerateAsync("dir", cancellationToken: cancelled.Token))
            {
            }
        });
    }

    [TestMethod]
    public async Task ALocalStreamsCommit_MapsAFailureAsAWrite()
    {
        var failure = new StoreFailureException("The upload failed.");
        using var provider = new FaultyProvider();
        var local = await provider.CreateLocalFileStreamAsync("f.txt", readOnly: false);
        await local.WriteAsync(new byte[] { 1, 2, 3 });
        provider.Fail = operation => operation == "write" ? failure : null;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => local.CommitAsync());
        Assert.AreSame(failure, e.InnerException);
        StringAssert.StartsWith(e.Message, "Writing 'f.txt'");
        await local.WriteAsync(new byte[] { 4 });
        Assert.AreSame(failure, (await Assert.ThrowsExactlyAsync<IOException>(async () => await local.DisposeAsync())).InnerException);

        var synchronous = await provider.CreateLocalFileStreamAsync("f.txt", readOnly: false);
        synchronous.WriteByte(1);
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(synchronous.Dispose).InnerException);
    }

    /// <summary>
    /// A provider over one file, <c>f.txt</c>, and a folder, <c>dir</c>, whose store fails as a test says.
    /// </summary>
    private sealed class FaultyProvider() : WriteableFileProvider<StubOptions>(new StubOptions())
    {
        /// <summary>What a store call throws, by operation (info, list, read, write, delete, sign, watch); null for none.</summary>
        public Func<string, Exception?>? Fail { get; set; }

        /// <summary>What the streams the provider returns throw once <see cref="FailAfter"/> bytes are read.</summary>
        public Exception? StreamFailure { get; set; }

        public int FailAfter { get; set; }

        /// <summary>Whether the streams fail their length, position, seeks and disposal too.</summary>
        public bool FailEverything { get; set; }

        /// <summary>What a listing throws after its first entry.</summary>
        public Exception? ListFailure { get; set; }

        /// <summary>What a listing throws when it is disposed.</summary>
        public Exception? ListDisposalFailure { get; set; }

        /// <summary>What a listing throws when it is asked for, before it is enumerated.</summary>
        public Exception? ListFailsAtOnce { get; set; }

        /// <summary>Whether the provider's predicates throw.</summary>
        public bool PredicatesFail { get; set; }

        protected override bool SupportsSignedUrl(SignedUrlAccess access) => true;

        protected override bool IsNotFound(Exception exception) =>
            PredicatesFail ? throw new InvalidOperationException("The predicate failed.") : exception is KeyNotFoundException;

        protected override bool IsAccessDenied(Exception exception) =>
            PredicatesFail ? throw new InvalidOperationException("The predicate failed.") : exception is SecurityException;

        /// <summary>A read the provider adds as a public member of its own.</summary>
        public Task<Stream> ReadVersionAsync(ProviderPath path, CancellationToken cancellationToken) =>
            ReadWithContractAsync(Resolve(path), async _ =>
            {
                await Check("read");
                return Body();
            }, cancellationToken);

        protected override async Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
        {
            await Check("info");
            return path.Name switch
            {
                "f.txt" => new WriteableFileInfo(this, path.Path, true, false, _data.Length, DateTimeOffset.UnixEpoch, etag: "e1"),
                "dir" => WriteableFileInfo.Folder(this, path.Path),
                _ => WriteableFileInfo.Missing(this, path.Path),
            };
        }

        protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
            ListFailsAtOnce is { } failure ? throw failure : new Listing(this, path);

        protected override async Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
        {
            await Check("read");
            return Body();
        }

        protected override async Task<Stream> OpenReadSeekableCoreAsync(ResolvedPath path, SeekableReadOptions options, CancellationToken cancellationToken)
        {
            await Check("read");
            return new FailingBody(_data, StreamFailure, FailAfter, FailEverything, seekable: true);
        }

        protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
        {
            await Check("write");
            await contents.CopyToAsync(Stream.Null, CancellationToken.None);
        }

        protected override async Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
        {
            await Check("delete");
            return true;
        }

        protected override async Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
        {
            await Check("sign");
            return new SignedUrl { Url = new Uri("https://faulty.test/" + path) };
        }

        protected override IChangeToken WatchCore(string filter) =>
            Fail?.Invoke("watch") is { } failure ? throw failure : new CancellationChangeToken(CancellationToken.None);

        private FailingBody Body() => new(_data, StreamFailure, FailAfter, FailEverything, seekable: false);

        // A store call: it fails as the test says, whatever the token (the tests decide what the caller cancelled).
        private async Task Check(string operation)
        {
            await Task.Yield();
            if (Fail?.Invoke(operation) is { } failure)
                throw failure;
        }

        // One entry, a folder, then what the test says: a failure of the next page, or of the disposal.
        private sealed class Listing(FaultyProvider owner, ResolvedPath path) : IAsyncEnumerable<IWriteableFileInfo>, IAsyncEnumerator<IWriteableFileInfo>
        {
            private int _index = -1;

            public IWriteableFileInfo Current { get; private set; } = null!;

            public IAsyncEnumerator<IWriteableFileInfo> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (++_index == 0)
                {
                    await owner.Check("list");
                    Current = WriteableFileInfo.Folder(owner, path.Child("sub").Path);
                    return true;
                }
                return owner.ListFailure is { } failure ? throw failure : false;
            }

            public ValueTask DisposeAsync() =>
                owner.ListDisposalFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }

    /// <summary>A body that yields its bytes, then fails as a test says.</summary>
    private sealed class FailingBody(byte[] data, Exception? failure, int failAfter, bool failEverything, bool seekable) : MemoryStream(data, writable: false)
    {
        private bool _failedDisposal;

        public override bool CanSeek => seekable;

        public override long Length => Fails ? throw failure! : seekable ? base.Length : throw new NotSupportedException();

        public override long Position
        {
            get => Fails ? throw failure! : seekable ? base.Position : throw new NotSupportedException();
            set
            {
                if (Fails)
                    throw failure!;
                if (!seekable)
                    throw new NotSupportedException();
                base.Position = value;
            }
        }

        private bool Fails => failEverything && failure is not null;

        public override long Seek(long offset, SeekOrigin origin) =>
            Fails ? throw failure! : seekable ? base.Seek(offset, origin) : throw new NotSupportedException();

        // MemoryStream's span read of a derived type comes back here, through the array read: the array read is the one.
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failure is null)
                return base.Read(buffer, offset, count);
            var left = failAfter - base.Position;
            if (left <= 0)
                throw failure;
            return base.Read(buffer, offset, (int)Math.Min(count, left));
        }

        public override int Read(Span<byte> buffer)
        {
            var bytes = new byte[buffer.Length];
            var read = Read(bytes, 0, bytes.Length);
            bytes.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }
            catch (Exception e)
            {
                return ValueTask.FromException<int>(e);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask DisposeAsync()
        {
            if (Fails && !_failedDisposal)
            {
                _failedDisposal = true;
                throw failure!;
            }
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Fails && !_failedDisposal)
            {
                _failedDisposal = true;
                throw failure!;
            }
            base.Dispose(disposing);
        }
    }
}
