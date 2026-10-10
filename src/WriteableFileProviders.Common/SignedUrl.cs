namespace WriteableFileProviders;

/// <summary>A URL that gives its holder access to one file for a limited time, without the provider's credentials.</summary>
public sealed class SignedUrl
{
    /// <summary>The URL.</summary>
    public required Uri Url { get; init; }

    /// <summary>The HTTP method the URL is for: GET to download, PUT to upload.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>When the URL stops working, or null when the service sets the lifetime and does not say.</summary>
    public DateTimeOffset? ExpiresOn { get; init; }

    /// <summary>Headers the request must send, such as the blob type of an Azure upload.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>How the URL grants access.</summary>
    public SignedUrlKind Kind { get; init; } = SignedUrlKind.Presigned;

    /// <summary>The URL.</summary>
    public override string ToString() => Url.ToString();
}
