using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// What a member lets out when Box fails: only the contract's types, with Box's error inside. A refused credential or
/// permission is <see cref="UnauthorizedAccessException"/>, anything else <see cref="IOException"/>.
/// </summary>
[TestClass]
public sealed class BoxErrorTests
{
    private static readonly TestCredentials _credentials = TestCredentials.Default;

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "access_denied_insufficient_permissions")]
    [DataRow(HttpStatusCode.Unauthorized, "unauthorized")]
    public async Task ARefusal_IsUnauthorizedAccess(HttpStatusCode status, string code)
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = _credentials.Provider(box);
        Assert.IsTrue((await provider.GetFileInfoAsync("a.txt")).Exists);

        box.Intercept = request => request.RequestUri!.AbsolutePath.Contains("/2.0/", StringComparison.Ordinal) ? Answer(status, code) : null;
        var info = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.GetFileInfoAsync("a.txt"));
        Assert.AreEqual((int)status, BoxErrors.Inner(info).ResponseInfo.StatusCode);
        CredentialAssert.CarriesNone(info);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("a.txt"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.UpdateFileAsync("a.txt", TestStreams.Text("y")));
    }

    [TestMethod]
    public async Task AnyOtherFailure_IsIOException()
    {
        var box = new FakeBox();
        using var provider = _credentials.Provider(box);
        box.Intercept = request => request.RequestUri!.AbsolutePath.Contains("/2.0/", StringComparison.Ordinal) ? Answer(HttpStatusCode.Conflict, "operation_blocked_temporary") : null;
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.GetDirectoryContentsAsync(""));
        Assert.AreEqual(409, BoxErrors.Inner(e).ResponseInfo.StatusCode);
        StringAssert.StartsWith(e.Message, "Listing the root folder");
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"type":"error","status":{{(int)status}},"code":"{{code}}"}""", Encoding.UTF8, "application/json"),
    };
}
