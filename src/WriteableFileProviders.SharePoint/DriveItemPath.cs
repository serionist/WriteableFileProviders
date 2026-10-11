using Microsoft.Graph.Drives.Item.Items.Item;
using Microsoft.Graph.Drives.Item.Items.Item.Children;
using Microsoft.Graph.Drives.Item.Items.Item.Content;
using Microsoft.Graph.Drives.Item.Items.Item.Copy;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using Microsoft.Graph.Drives.Item.Root;
using Microsoft.Kiota.Abstractions;

namespace WriteableFileProviders.SharePoint;

/// <summary>
/// The requests on a drive item addressed by its path, <c>root:/{path}:</c>, with the URLs Graph's own
/// <c>Root.ItemWithPath(path)</c> builds. <c>ItemWithPath</c> reads and rewrites the request builders' URL templates
/// through reflection on their private fields, which trimming and native AOT break; the builders here are subclasses
/// that set their own template.
/// </summary>
/// <remarks>
/// Each sub-request keeps the last segment of its own template (<c>/content</c>, <c>/children{?...}</c>) after the
/// item's path, as <c>ItemWithPath</c>'s builders do. The path is escaped as <c>ItemWithPath</c> escapes it: appended
/// to the root's URL through <see cref="UriBuilder"/>, which escapes what a URL path cannot hold.
/// </remarks>
internal sealed class DriveItemPath
{
    private readonly Dictionary<string, object> _pathParameters;
    private readonly IRequestAdapter _requestAdapter;
    private readonly string _itemTemplate;

    /// <summary>Addresses a drive item by its path.</summary>
    /// <param name="requestAdapter">The Graph client's request adapter.</param>
    /// <param name="driveId">The drive.</param>
    /// <param name="path">The item's path in the drive, not empty: segments joined with <c>/</c>.</param>
    public DriveItemPath(IRequestAdapter requestAdapter, string driveId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _requestAdapter = requestAdapter;
        // What the Graph client hands each builder under Drives[driveId].
        _pathParameters = new() { ["baseurl"] = requestAdapter.BaseUrl ?? "", ["drive%2Did"] = driveId };
        var root = new RootBuilder(_pathParameters, requestAdapter);
        var url = new UriBuilder(root.ToGetRequestInformation().URI.OriginalString);
        url.Path += $":{(path.StartsWith('/') ? path : "/" + path)}:";
        var escapedPath = url.Uri.OriginalString.Split(':', StringSplitOptions.RemoveEmptyEntries)[^1];
        _itemTemplate = root.Template.Replace("root", $"root:{escapedPath}:", StringComparison.Ordinal);
    }

    /// <summary>
    /// The item: get, update, delete. Only those: its own sub-request properties (<c>Children</c>, <c>Content</c>...)
    /// would address <c>items/</c> with no item, so sub-requests go through this class's properties.
    /// </summary>
    public DriveItemItemRequestBuilder Item => new ItemBuilder(_pathParameters, _requestAdapter, _itemTemplate);

    /// <summary>The folder's children.</summary>
    public ChildrenRequestBuilder Children => new ChildrenBuilder(_pathParameters, _requestAdapter, _itemTemplate);

    /// <summary>The file's content.</summary>
    public ContentRequestBuilder Content => new ContentBuilder(_pathParameters, _requestAdapter, _itemTemplate);

    /// <summary>A copy of the item.</summary>
    public CopyRequestBuilder Copy => new CopyBuilder(_pathParameters, _requestAdapter, _itemTemplate);

    /// <summary>An upload session for the file.</summary>
    public CreateUploadSessionRequestBuilder CreateUploadSession =>
        new UploadSessionBuilder(_pathParameters, _requestAdapter, _itemTemplate);

    // The item's template up to the colon that closes its path, then the sub-request's own last segment.
    private static string Below(string itemTemplate, string ownTemplate) => string.Concat(
        itemTemplate.AsSpan(0, itemTemplate.LastIndexOf(':') + 1), ownTemplate.AsSpan(ownTemplate.LastIndexOf('/')));

    private sealed class RootBuilder(Dictionary<string, object> pathParameters, IRequestAdapter requestAdapter)
        : RootRequestBuilder(pathParameters, requestAdapter)
    {
        public string Template => UrlTemplate;
    }

    private sealed class ItemBuilder : DriveItemItemRequestBuilder
    {
        public ItemBuilder(Dictionary<string, object> pathParameters, IRequestAdapter requestAdapter, string template)
            : base(pathParameters, requestAdapter) => UrlTemplate = template;
    }

    private sealed class ChildrenBuilder : ChildrenRequestBuilder
    {
        public ChildrenBuilder(Dictionary<string, object> pathParameters, IRequestAdapter requestAdapter, string item)
            : base(pathParameters, requestAdapter) => UrlTemplate = Below(item, UrlTemplate);
    }

    private sealed class ContentBuilder : ContentRequestBuilder
    {
        public ContentBuilder(Dictionary<string, object> pathParameters, IRequestAdapter requestAdapter, string item)
            : base(pathParameters, requestAdapter) => UrlTemplate = Below(item, UrlTemplate);
    }

    private sealed class CopyBuilder : CopyRequestBuilder
    {
        public CopyBuilder(Dictionary<string, object> pathParameters, IRequestAdapter requestAdapter, string item)
            : base(pathParameters, requestAdapter) => UrlTemplate = Below(item, UrlTemplate);
    }

    private sealed class UploadSessionBuilder : CreateUploadSessionRequestBuilder
    {
        public UploadSessionBuilder(Dictionary<string, object> pathParameters, IRequestAdapter requestAdapter, string item)
            : base(pathParameters, requestAdapter) => UrlTemplate = Below(item, UrlTemplate);
    }
}
