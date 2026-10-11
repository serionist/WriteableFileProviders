using System.Net;
using System.Text;
using Azure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// What a member lets out where Azure fails part of the way, on Azurite with an answer injected: only the contract's
/// types, with Azure's exception inside.
/// </summary>
public sealed partial class AzureBlobProviderTests
{
    // ----- Errors (the error contract) -----

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, "BlobNotFound", typeof(FileNotFoundException))]
    [DataRow(HttpStatusCode.PreconditionFailed, "ConditionNotMet", typeof(IOException))]
    public async Task ResumedRead_ThatFindsTheFileGoneOrChanged_IsTheContractsType(HttpStatusCode status, string code, Type expected)
    {
        using var provider = Provider();
        await Write(provider, "f.txt", "0123456789");
        var properties = (await _raw.GetBlobClient(Name("f.txt")).GetPropertiesAsync()).Value;
        var downloads = 0;
        _handler.Intercept = r =>
        {
            if (r.Method != HttpMethod.Get || AzureHandler.BlobName(r) != Name("f.txt") || AzureHandler.Comp(r) is not null)
                return null;
            // The first download breaks after 3 bytes, so the SDK resumes it from there, pinned to the version it read;
            // the resumed request finds the file deleted or changed.
            return ++downloads == 1 ? BrokenDownload(properties.ETag, properties.LastModified, "0123456789", 3) : AzureHandler.Error(status, code);
        };
        await using var stream = await provider.ReadFileAsync("f.txt");
        var e = await Assert.ThrowsAsync<Exception>(async () => await new StreamReader(stream).ReadToEndAsync());
        Assert.AreEqual(expected, e.GetType());
        Assert.AreEqual((int)status, Assert.IsInstanceOfType<RequestFailedException>(e.InnerException).Status);
        Assert.AreEqual(2, downloads, "The download was resumed once");
    }

    [TestMethod]
    public async Task Move_WhoseCopyIsRefused_IsUnauthorizedAccess()
    {
        using var provider = Provider();
        await Write(provider, "a.txt", "a");
        _handler.Intercept = r => AzureHandler.IsCopy(r) ? AzureHandler.Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch") : null;
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.MoveAsync("a.txt", "b.txt"));
        Assert.AreEqual(403, Assert.IsInstanceOfType<RequestFailedException>(e.InnerException).Status);
        _handler.Intercept = null;
        Assert.AreEqual("a", await ReadRaw("a.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists);
    }

    [TestMethod]
    public async Task Move_WhoseSourceDeleteIsRefused_IsUnauthorizedAccess_AndRemovesTheCopy()
    {
        using var provider = Provider();
        await Write(provider, "a.txt", "a");
        _handler.Intercept = r => r.Method == HttpMethod.Delete && AzureHandler.BlobName(r) == Name("a.txt")
            ? AzureHandler.Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")
            : null;
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.MoveAsync("a.txt", "b.txt"));
        Assert.AreEqual(403, Assert.IsInstanceOfType<RequestFailedException>(e.InnerException).Status);
        StringAssert.Contains(e.Message, "was removed");
        _handler.Intercept = null;
        Assert.AreEqual("a", await ReadRaw("a.txt"));
        Assert.IsFalse((await provider.GetFileInfoAsync("b.txt")).Exists, "The copy was undone");
    }

    [TestMethod]
    public async Task MissingContainer_WhoseCreationIsRefused_IsUnauthorizedAccess()
    {
        var name = Azurite.NewContainerName();
        _handler.Intercept = r => r.Method == HttpMethod.Put && r.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal)
            ? AzureHandler.Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")
            : null;
        using var provider = new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { CreateContainerIfMissing = true }, Azurite.Container(name, _handler));
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write(provider, "a.txt", "x"));
        Assert.AreEqual(403, Assert.IsInstanceOfType<RequestFailedException>(e.InnerException).Status);
        Assert.IsFalse((await Azurite.Container(name).ExistsAsync()).Value);
    }

    // A download's answer whose body breaks after some bytes, as a connection that resets does.
    private static HttpResponseMessage BrokenDownload(ETag etag, DateTimeOffset lastModified, string content, int breakAfter)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(bytes, breakAfter)) };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        response.Content.Headers.LastModified = lastModified;
        response.Headers.TryAddWithoutValidation("ETag", etag.ToString("H"));
        response.Headers.Add("x-ms-blob-type", "BlockBlob");
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-version", "2025-01-05");
        return response;
    }

    private sealed class BreakingStream(byte[] data, int breakAfter) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Position >= breakAfter ? throw new IOException("The connection was reset (injected).") : base.Read(buffer, offset, (int)Math.Min(count, breakAfter - Position));

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
            catch (IOException e)
            {
                return ValueTask.FromException<int>(e);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
