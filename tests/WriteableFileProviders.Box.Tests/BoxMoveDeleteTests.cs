using System.Net;
using System.Text;
using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;

namespace WriteableFileProviders.Box.Tests;

/// <summary>Deletes, moves and copies: server-side calls, and Box's 404, 409 and blocked answers.</summary>
[TestClass]
public sealed class BoxMoveDeleteTests
{
    private static readonly TestCredentials _credentials = TestCredentials.Default;

    [TestMethod]
    public async Task Delete_FolderIsRecursive_AndAFileIsOneCall()
    {
        var box = new FakeBox();
        var dir = box.AddFolder(FakeBox.RootId, "dir");
        var file = box.AddFile(dir, "a.txt", "x");
        box.AddFile(box.AddFolder(dir, "sub"), "b.txt", "y");
        using var provider = _credentials.Provider(box);

        Assert.IsTrue(await provider.DeleteAsync("dir/a.txt"));
        Assert.AreEqual(1, box.Requests.Count(r => r.Method == "DELETE" && r.Path == $"/2.0/files/{file}"));
        Assert.IsTrue(await provider.DeleteAsync("DIR"));
        StringAssert.Contains(box.Requests.Single(r => r.Method == "DELETE" && r.Path == $"/2.0/folders/{dir}").Uri.Query, "recursive=True");
        Assert.IsNull(box.IdOf("dir"));
    }

    [TestMethod]
    public async Task Delete_GoneMeanwhile_IsFalse()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "a.txt", "x");
        box.Intercept = request =>
        {
            if (request.Method == HttpMethod.Delete)
                box.Delete(id);
            return null;
        };
        using var provider = _credentials.Provider(box);

        Assert.IsFalse(await provider.DeleteAsync("a.txt"));
    }

    [TestMethod]
    public async Task Delete_BlockedForAMoment_IsTriedAgain()
    {
        var box = new FakeBox();
        box.AddFolder(FakeBox.RootId, "dir");
        var blocked = 2;
        box.Intercept = request => request.Method == HttpMethod.Delete && blocked-- > 0 ? Blocked() : null;
        using var provider = _credentials.Provider(box);

        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.AreEqual(3, box.Requests.Count(r => r.Method == "DELETE"));
    }

    [TestMethod]
    public async Task Delete_BlockedForGood_Throws()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "x");
        box.Intercept = request => request.Method == HttpMethod.Delete ? Blocked() : null;
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.DeleteAsync("a.txt"));
        Assert.AreEqual("operation_blocked_temporary", e.ResponseInfo.Code);
        Assert.AreEqual(3, box.Requests.Count(r => r.Method == "DELETE"));
    }

    [TestMethod]
    public async Task Move_IsOneServerSideUpdate_KeepingTheFile()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = _credentials.Provider(box);

        await provider.MoveAsync("a.txt", "new/dir/b.txt");

        Assert.AreEqual(id, box.IdOf("new/dir/b.txt"));
        Assert.AreEqual(1, box.Requests.Count(r => r.Method == "PUT" && r.Path == $"/2.0/files/{id}"));
        Assert.IsFalse(box.Requests.Any(r => r.Uri.Host == "upload.box.test" || r.Uri.Host == "dl.box.test"), "Nothing is downloaded or uploaded");
    }

    [TestMethod]
    public async Task Move_DestinationTakenMeanwhile_IsFileExists()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "a");
        var raced = false;
        box.Intercept = request =>
        {
            if (!raced && request.Method == HttpMethod.Put)
            {
                raced = true;
                box.AddFile(FakeBox.RootId, "b.txt", "theirs");
            }
            return null;
        };
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.MoveAsync("a.txt", "b.txt"));
        Assert.AreEqual("a", box.ContentOf("a.txt"));
        Assert.AreEqual("theirs", box.ContentOf("b.txt"));
    }

    [TestMethod]
    public async Task Move_ToANameBoxRefuses_IsArgumentException()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "a");
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.MoveAsync("a.txt", "b.txt "));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.MoveAsync("a.txt", "dir /b.txt"));
        Assert.AreEqual("a", box.ContentOf("a.txt"));
    }

    [TestMethod]
    public async Task Move_CaseOnly_WhenBoxRefusesIt_GoesThroughATemporaryName()
    {
        var box = new FakeBox { RefuseCaseOnlyRename = true };
        var id = box.AddFile(FakeBox.RootId, "a.txt", "a");
        using var provider = _credentials.Provider(box);

        await provider.MoveAsync("a.txt", "A.txt");

        Assert.AreEqual("A.txt", box.NameOf(id));
        Assert.AreEqual(3, box.Requests.Count(r => r.Method == "PUT"), "Refused, to the temporary name, to the new name");
    }

    [TestMethod]
    public async Task Move_CaseOnly_FinalRenameFails_PutsTheNameBack()
    {
        var box = new FakeBox { RefuseCaseOnlyRename = true };
        var id = box.AddFile(FakeBox.RootId, "a.txt", "a");
        var puts = 0;
        box.Intercept = request => request.Method == HttpMethod.Put && ++puts == 3 ? BadRequest() : null;
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<BoxApiException>(() => provider.MoveAsync("a.txt", "A.txt"));

        Assert.AreEqual("a.txt", box.NameOf(id));
    }

    [TestMethod]
    public async Task Move_CaseOnly_PuttingTheNameBackFails_SaysWhereTheFileIs()
    {
        var box = new FakeBox { RefuseCaseOnlyRename = true };
        var id = box.AddFile(FakeBox.RootId, "a.txt", "a");
        var puts = 0;
        box.Intercept = request => request.Method == HttpMethod.Put && ++puts >= 3 ? BadRequest() : null;
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.MoveAsync("a.txt", "A.txt"));

        StringAssert.Contains(e.Message, box.NameOf(id));
        StringAssert.StartsWith(box.NameOf(id), ".wfp-rename-");
        Assert.IsInstanceOfType<AggregateException>(e.InnerException);
    }

    [TestMethod]
    public async Task Move_CaseOnly_OfALongName_FitsTheTemporaryName()
    {
        var box = new FakeBox { RefuseCaseOnlyRename = true };
        var name = new string('a', 250) + ".txt";
        var id = box.AddFile(FakeBox.RootId, name, "a");
        using var provider = _credentials.Provider(box);

        await provider.MoveAsync(name, name.ToUpperInvariant());

        Assert.AreEqual(name.ToUpperInvariant(), box.NameOf(id));
    }

    [TestMethod]
    public async Task Move_CaseOnly_NameRefusedOnTheWay_IsArgumentException()
    {
        var box = new FakeBox { RefuseCaseOnlyRename = true };
        var id = box.AddFile(FakeBox.RootId, "a.txt", "a");
        var puts = 0;
        box.Intercept = request => request.Method == HttpMethod.Put && ++puts == 3
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"type":"error","status":400,"code":"item_name_too_long"}""", Encoding.UTF8, "application/json"),
            }
            : null;
        using var provider = _credentials.Provider(box);

        var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.MoveAsync("a.txt", "A.txt"));

        Assert.AreEqual("path", e.ParamName);
        Assert.AreEqual("a.txt", box.NameOf(id), "The name is put back");
    }

    [TestMethod]
    public async Task Copy_IsServerSide_FromTheVersionFound()
    {
        var box = new FakeBox();
        var id = box.AddFile(FakeBox.RootId, "a.txt", "x");
        using var provider = _credentials.Provider(box);

        await provider.CopyAsync("a.txt", "copies/b.txt");

        Assert.AreEqual("x", box.ContentOf("copies/b.txt"));
        Assert.AreEqual("x", box.ContentOf("a.txt"));
        var copy = box.Requests.Single(r => r.Path == $"/2.0/files/{id}/copy");
        Assert.AreEqual("POST", copy.Method);
        Assert.IsFalse(box.Requests.Any(r => r.Uri.Host == "upload.box.test" || r.Uri.Host == "dl.box.test"), "Nothing is downloaded or uploaded");
    }

    [TestMethod]
    public async Task Copy_DestinationTakenMeanwhile_IsFileExists()
    {
        var box = new FakeBox();
        box.AddFile(FakeBox.RootId, "a.txt", "a");
        var raced = false;
        box.Intercept = request =>
        {
            if (!raced && request.RequestUri!.AbsolutePath.EndsWith("/copy", StringComparison.Ordinal))
            {
                raced = true;
                box.AddFile(FakeBox.RootId, "b.txt", "theirs");
            }
            return null;
        };
        using var provider = _credentials.Provider(box);

        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("a.txt", "b.txt"));
        Assert.AreEqual("theirs", box.ContentOf("b.txt"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.CopyAsync("a.txt", "c.txt "));
    }

    private static HttpResponseMessage Blocked() =>
        new(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"type":"error","status":409,"code":"operation_blocked_temporary","message":"Blocked."}""", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage BadRequest() =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"type":"error","status":400,"code":"bad_request","message":"No."}""", Encoding.UTF8, "application/json"),
        };
}
