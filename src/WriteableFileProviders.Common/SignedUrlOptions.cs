namespace WriteableFileProviders;

/// <summary>What <see cref="IWriteableFileProvider.GetSignedUrlAsync"/> issues.</summary>
public sealed record SignedUrlOptions
{
    /// <summary>
    /// How long the URL lives: more than zero. A URL never lives longer than this; a provider that cannot sign this long
    /// throws <see cref="NotSupportedException"/>.
    /// </summary>
    public required TimeSpan Validity { get; init; }

    /// <summary>What the URL allows; <see cref="SignedUrlAccess.Read"/> by default.</summary>
    public SignedUrlAccess Permission { get; init; } = SignedUrlAccess.Read;

    /// <summary>
    /// For a read URL, the file name a browser saves the download as. A provider that cannot set it throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public string? DownloadFileName { get; init; }

    /// <summary>
    /// For a read URL, the content type the response carries; for an upload URL, the content type the upload must send.
    /// A provider that cannot set it throws <see cref="NotSupportedException"/>.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Accept a URL that may stop working before <see cref="Validity"/> ends, such as a service's own short-lived URL;
    /// its <see cref="SignedUrl.ExpiresOn"/> says when, if known. Without it, such a provider throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public bool AllowShorterValidity { get; init; }
}
