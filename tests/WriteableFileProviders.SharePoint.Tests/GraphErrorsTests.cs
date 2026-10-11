using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>How a Graph failure surfaces (F66): only a 404 means missing, and nothing else is hidden.</summary>
[TestClass]
public sealed class GraphErrorsTests
{
    [TestMethod]
    public void Wrap_KeepsTheStatusAndTheCodes()
    {
        var error = new ODataError
        {
            ResponseStatusCode = 423,
            Error = new MainError { Code = "notAllowed", Message = "Locked", InnerError = new InnerError { AdditionalData = new Dictionary<string, object> { ["code"] = "resourceLocked" } } },
        };
        var wrapped = GraphErrors.Wrap(error, "Writing 'a'");
        Assert.IsInstanceOfType<IOException>(wrapped);
        Assert.AreEqual("Writing 'a' failed: 423 notAllowed (resourceLocked): Locked.", wrapped.Message);
        Assert.AreSame(error, wrapped.InnerException);

        Assert.IsInstanceOfType<UnauthorizedAccessException>(GraphErrors.Wrap(new ApiException { ResponseStatusCode = 401 }, "x"));
        Assert.IsInstanceOfType<PreconditionFailedException>(GraphErrors.Wrap(new ApiException { ResponseStatusCode = 412 }, "x"));
        StringAssert.Contains(GraphErrors.Wrap(new AggregateException(new InvalidOperationException()), "x").Message, "with errors");
        StringAssert.Contains(GraphErrors.Wrap(new HttpRequestException("reset"), "x").Message, "reset");
    }

    [TestMethod]
    public void FromResponse_MapsTheStatus()
    {
        Assert.IsInstanceOfType<UnauthorizedAccessException>(GraphErrors.FromResponse(401, null, "x"));
        Assert.IsInstanceOfType<PreconditionFailedException>(GraphErrors.FromResponse(412, GraphFake.ErrorJson("resourceModified", "m"), "x"));
        Assert.AreEqual("x failed: 500 generalException.", GraphErrors.FromResponse(500, GraphFake.ErrorJson("generalException", "m"), "x").Message);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(" ")]
    [DataRow("[]")]
    [DataRow("not json")]
    [DataRow("{\"error\":\"text\"}")]
    [DataRow("{\"error\":{\"code\":3}}")]
    public void CodeOf_ReadsOnlyAGraphError(string? body) => Assert.IsNull(GraphErrors.CodeOf(body));

    [TestMethod]
    public void IsFailure_LetsCancellationAndDisposalThrough()
    {
        Assert.IsFalse(GraphErrors.IsFailure(new OperationCanceledException()));
        Assert.IsFalse(GraphErrors.IsFailure(new ObjectDisposedException("x")));
        Assert.IsTrue(GraphErrors.IsFailure(new InvalidOperationException("Content type text/html does not have a factory registered to be parsed")));
        Assert.IsTrue(GraphErrors.IsFailure(new System.Text.Json.JsonException("Malformed")));
        Assert.IsNull(GraphErrors.Status(new IOException()));
        Assert.IsNull(GraphErrors.Code(new ApiException()));
    }
}
