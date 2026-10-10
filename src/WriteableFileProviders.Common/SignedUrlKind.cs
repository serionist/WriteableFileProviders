namespace WriteableFileProviders;

/// <summary>How a signed URL grants access.</summary>
public enum SignedUrlKind
{
    /// <summary>Signed by the provider with its credential: a SAS, a presigned or V4 signed URL.</summary>
    Presigned,

    /// <summary>A URL the service hands out with access built in, and a lifetime the service sets.</summary>
    PreAuthenticated,

    /// <summary>A token for the WriteableFileProviders.AspNetCore gateway, which serves the file itself.</summary>
    Gateway,
}
