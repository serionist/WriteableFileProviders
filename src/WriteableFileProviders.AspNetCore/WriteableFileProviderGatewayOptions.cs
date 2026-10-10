namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// The options of the signed-URL gateway (<see cref="WriteableFileProviderGatewayServiceCollectionExtensions.AddWriteableFileProviderGateway"/>)
/// and of the URL service (<see cref="IWriteableFileUrlService"/>). They are validated when the app starts.
/// </summary>
/// <remarks>
/// A gateway URL is <c>{PublicBaseUri}{BasePath}/{token}/{fileName}</c>. The token is an encrypted, signed and
/// time-limited ASP.NET Core Data Protection payload that names the provider, the file and what the URL allows; the file
/// name is cosmetic and ignored. Tokens travel in the URL path, so whoever holds a URL can use it until it expires, and
/// anything that logs request paths logs them: see the package README on redaction.
/// </remarks>
public sealed class WriteableFileProviderGatewayOptions
{
    /// <summary>The longest validity any gateway URL can be given: 7 days.</summary>
    public static TimeSpan MaxValidityLimit { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Required: where the gateway is reachable from outside, including any path base the app runs under behind a
    /// proxy. Gateway URLs are built from it and never from the request's <c>Host</c> header, so a forged header cannot
    /// redirect a URL. It must be absolute and <c>https</c>; plain <c>http</c> is accepted only for a loopback host, or
    /// with <see cref="AllowInsecureHttp"/>.
    /// </summary>
    /// <remarks>
    /// Use a host name of its own that the app's cookies are not sent to, such as <c>https://files.example.com/</c> beside
    /// <c>https://app.example.com/</c> (cookies scoped to the app's host, not to <c>.example.com</c>): whatever a download
    /// holds then never runs, or reads cookies, on the app's origin. The gateway's own headers (a sandboxing
    /// <c>Content-Security-Policy</c>, <c>nosniff</c>, <c>attachment</c>) are the second line.
    /// </remarks>
    public Uri? PublicBaseUri { get; set; }

    /// <summary>
    /// The path the gateway's endpoints are mapped under; <c>/files</c> by default. It starts with <c>/</c>, does not end
    /// with one, and holds only letters, digits, <c>-</c>, <c>.</c>, <c>_</c>, <c>~</c> and <c>/</c>. Choose one that
    /// does not clash with the app's other routes.
    /// </summary>
    public string BasePath { get; set; } = "/files";

    /// <summary>
    /// The longest validity of a gateway download (<see cref="SignedUrlAccess.Read"/>) URL; 7 days by default, and never
    /// more than <see cref="MaxValidityLimit"/>. A longer request throws <see cref="NotSupportedException"/>, or with
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/> gets a URL that lives this long.
    /// </summary>
    public TimeSpan MaxReadValidity { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The longest validity of a gateway upload (<see cref="SignedUrlAccess.Create"/> and
    /// <see cref="SignedUrlAccess.Write"/>) URL; 1 hour by default, and never more than <see cref="MaxValidityLimit"/>.
    /// A longer request throws <see cref="NotSupportedException"/>, or with
    /// <see cref="SignedUrlOptions.AllowShorterValidity"/> gets a URL that lives this long.
    /// </summary>
    public TimeSpan MaxUploadValidity { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether an upload URL works for one upload only; true by default. The gateway records each upload token it accepts
    /// in the <see cref="IGatewayReplayStore"/>, whose default holds in the memory of one app instance: an app that runs
    /// on several instances registers a shared store, or sets this to false.
    /// </summary>
    public bool OneTimeUploadTokens { get; set; } = true;

    /// <summary>
    /// The largest body an upload may send, in bytes; 100 MiB by default. A larger declared <c>Content-Length</c> is
    /// refused with 413 before anything is read, and a body that grows past it is cut off with 413, leaving the file as
    /// it was. The gateway raises the server's own request body limit to this value for its uploads.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// Whether <see cref="IWriteableFileUrlService"/> always issues gateway URLs, even for a provider that signs URLs
    /// natively; false by default, so the provider's own URL is used where it has one.
    /// </summary>
    public bool PreferGateway { get; set; }

    /// <summary>
    /// Accept a plain <c>http</c> <see cref="PublicBaseUri"/> on a host that is not loopback. Tokens are bearer
    /// credentials: over plain HTTP anyone on the way can read and reuse them. False by default.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }
}
