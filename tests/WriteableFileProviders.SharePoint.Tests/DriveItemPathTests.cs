using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Copy;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>
/// <see cref="DriveItemPath"/> sends every request to the URL Graph's own <c>Root.ItemWithPath(path)</c> builds, which it
/// replaces in trimmed and native AOT apps. Paths arrive as the provider passes them, with every '%' already escaped.
/// </summary>
[TestClass]
public sealed class DriveItemPathTests
{
    [TestMethod]
    [DataRow("a.txt")]
    [DataRow("folder/b c.txt")]
    [DataRow("/leading/slash.txt")]
    [DataRow("percent/%2541 and %25.txt")]
    [DataRow("x/#hash?query.txt")]
    [DataRow("ü/日本語 файл.txt")]
    [DataRow("plus+&=;,$@!'()~.txt")]
    [DataRow("{brace}[bracket]^`.txt")]
    [DataRow("deep/er/still/file")]
    public void Every_request_has_ItemWithPaths_url(string path)
    {
        foreach (var baseUrl in new[] { null, GraphFake.BaseUrl })
        {
            using var graph = new GraphServiceClient(new HttpClient(), new AnonymousAuthenticationProvider(), baseUrl);
            var theirs = graph.Drives["b!drive-id"].Root.ItemWithPath(path);
            var ours = new DriveItemPath(graph.RequestAdapter, "b!drive-id", path);

            string[] fields = ["id", "name", "eTag"];
            AssertSameUrl(theirs.ToGetRequestInformation(c => c.QueryParameters.Select = fields), ours.Item.ToGetRequestInformation(c => c.QueryParameters.Select = fields));
            AssertSameUrl(theirs.ToPatchRequestInformation(new()), ours.Item.ToPatchRequestInformation(new()));
            AssertSameUrl(theirs.ToDeleteRequestInformation(), ours.Item.ToDeleteRequestInformation());
            AssertSameUrl(
                theirs.Children.ToGetRequestInformation(c => { c.QueryParameters.Select = fields; c.QueryParameters.Top = 200; }),
                ours.Children.ToGetRequestInformation(c => { c.QueryParameters.Select = fields; c.QueryParameters.Top = 200; }));
            AssertSameUrl(theirs.Content.ToGetRequestInformation(), ours.Content.ToGetRequestInformation());
            AssertSameUrl(theirs.Content.ToPutRequestInformation(Stream.Null), ours.Content.ToPutRequestInformation(Stream.Null));
            AssertSameUrl(theirs.Copy.ToPostRequestInformation(new CopyPostRequestBody()), ours.Copy.ToPostRequestInformation(new CopyPostRequestBody()));
            AssertSameUrl(
                theirs.CreateUploadSession.ToPostRequestInformation(new CreateUploadSessionPostRequestBody()),
                ours.CreateUploadSession.ToPostRequestInformation(new CreateUploadSessionPostRequestBody()));
        }
    }

    [TestMethod]
    public void An_empty_path_is_refused() =>
        Assert.ThrowsExactly<ArgumentException>(() => new DriveItemPath(new GraphServiceClient(new HttpClient(), new AnonymousAuthenticationProvider()).RequestAdapter, "drive", ""));

    private static void AssertSameUrl(RequestInformation expected, RequestInformation actual)
    {
        Assert.AreEqual(expected.HttpMethod, actual.HttpMethod);
        Assert.AreEqual(expected.URI.OriginalString, actual.URI.OriginalString);
    }
}
