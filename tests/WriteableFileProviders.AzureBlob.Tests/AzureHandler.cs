using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Azure.Core;

namespace WriteableFileProviders.AzureBlob.Tests;

/// <summary>
/// A seam in the SDK's HTTP pipeline for the tests: it records every request, emulates what Azure enforces and Azurite
/// does not, and lets a test answer a request itself.
/// </summary>
/// <remarks>
/// Azurite lets a Put Blob or a Copy Blob replace an append blob, and commits any number of blocks. Azure refuses both
/// (409 <c>InvalidBlobType</c>: a blob's type changes only by deleting it; 409 <c>BlockCountExceedsLimit</c> past
/// 50,000 committed blocks). With <see cref="EmulateAzure"/>, this handler answers those requests as Azure does, so a
/// provider that relied on Azurite's leniency fails here.
/// </remarks>
internal sealed partial class AzureHandler : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, byte> _appendBlobs = new(StringComparer.Ordinal);

    /// <summary>Every request, as "METHOD name?comp=x" ("copy" for a Copy Blob, the blob type for a Put Blob).</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>Whether to refuse what Azure refuses and Azurite allows.</summary>
    public bool EmulateAzure { get; init; } = true;

    /// <summary>The committed block limit to enforce: Azure's 50,000, or the provider's lowered one.</summary>
    public int MaxBlocks { get; init; } = 50_000;

    /// <summary>Called before a request is sent: a response it returns answers the request, which is not sent.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>The requests logged for one blob, by its name in the container.</summary>
    public List<string> For(string blobName) => [.. Log.Where(l => l.Split(' ')[1].Split('?')[0] == blobName)];

    /// <summary>An error response in Azure Storage's form.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{code} (test)</Message></Error>", Encoding.UTF8, "application/xml"),
        };
        response.Headers.Add("x-ms-error-code", code);
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        return response;
    }

    /// <summary>The blob a request is for: its name in the container, or empty for a service or container request.</summary>
    public static string BlobName(HttpRequestMessage request)
    {
        var parts = request.RequestUri!.AbsolutePath.Split('/');
        return parts.Length > 3 ? Uri.UnescapeDataString(string.Join('/', parts[3..])) : "";
    }

    public static string? Comp(HttpRequestMessage request) => HttpUtility.ParseQueryString(request.RequestUri!.Query)["comp"];

    public static bool IsCopy(HttpRequestMessage request) => request.Headers.Contains("x-ms-copy-source");

    public static string? BlobType(HttpRequestMessage request) =>
        request.Headers.TryGetValues("x-ms-blob-type", out var values) ? values.FirstOrDefault() : null;

    // A sync call of the SDK (a test's own race) goes through the same rules; DelegatingHandler would skip them.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var name = BlobName(request);
        var comp = Comp(request);
        var copy = IsCopy(request);
        var type = BlobType(request);
        Log.Enqueue($"{request.Method} {name}{(comp is null ? "" : "?comp=" + comp)}{(copy ? " copy" : "")}{(type is null || comp is not null ? "" : " " + type)}");
        if (Intercept?.Invoke(request) is { } answer)
            return answer;
        var key = request.RequestUri!.AbsolutePath;
        if (EmulateAzure && request.Method == HttpMethod.Put)
        {
            var replacesContent = comp == "blocklist" || (comp is null && (copy || type == "BlockBlob"));
            if (replacesContent && _appendBlobs.ContainsKey(key))
                return Error(HttpStatusCode.Conflict, "InvalidBlobType");
            if (comp == "blocklist" && request.Content is not null)
            {
                var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var blocks = BlockEntry().Count(Encoding.UTF8.GetString(body));
                if (blocks > MaxBlocks)
                    return Error(HttpStatusCode.Conflict, "BlockCountExceedsLimit");
                var copyOfBody = new ByteArrayContent(body);
                foreach (var header in request.Content.Headers)
                    copyOfBody.Headers.TryAddWithoutValidation(header.Key, header.Value);
                request.Content = copyOfBody;
            }
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode && comp is null)
        {
            if (request.Method == HttpMethod.Delete)
                _appendBlobs.TryRemove(key, out _);
            else if (request.Method == HttpMethod.Put && type == "AppendBlob")
                _appendBlobs[key] = 0;
        }
        return response;
    }

    [GeneratedRegex("<(Latest|Committed|Uncommitted)>")]
    private static partial Regex BlockEntry();
}

/// <summary>An HTTP handler that answers every request itself: an offline Azure for unit tests.</summary>
/// <param name="respond">The answer to each request, given its body.</param>
internal sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<HttpRequestMessage, string> _bodies = new();

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => respond(request))
    {
    }

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>The body a request sent, read while it was sent.</summary>
    public string Body(HttpRequestMessage request) => _bodies.GetValueOrDefault(request, "");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        _bodies[request] = body;
        Requests.Enqueue(request);
        return respond(request, body);
    }

    /// <summary>The answer to a Get Blob Properties of an existing block blob.</summary>
    public static HttpResponseMessage BlobProperties(long length = 5)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = length;
        response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0x8D00000000000A1\"");
        response.Headers.Add("x-ms-blob-type", "BlockBlob");
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        return response;
    }

    /// <summary>The answer to Get User Delegation Key, with the times the test chooses.</summary>
    public static HttpResponseMessage DelegationKey(DateTimeOffset start, DateTimeOffset expiry)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><UserDelegationKey>"
            + "<SignedOid>00000000-0000-0000-0000-000000000001</SignedOid><SignedTid>00000000-0000-0000-0000-000000000002</SignedTid>"
            + $"<SignedStart>{start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</SignedStart><SignedExpiry>{expiry.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</SignedExpiry>"
            + "<SignedService>b</SignedService><SignedVersion>2026-10-06</SignedVersion>"
            + $"<Value>{Convert.ToBase64String(new byte[32])}</Value></UserDelegationKey>";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        return response;
    }

    /// <summary>The Start and Expiry a Get User Delegation Key request asked for, from its body.</summary>
    public static (DateTimeOffset Start, DateTimeOffset Expiry) RequestedKeyTimes(string body)
    {
        static DateTimeOffset Between(string text, string tag) =>
            DateTimeOffset.Parse(text.Split($"<{tag}>")[1].Split($"</{tag}>")[0], System.Globalization.CultureInfo.InvariantCulture);
        return (Between(body, "Start"), Between(body, "Expiry"));
    }

    public static bool IsDelegationKeyRequest(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.Query.Contains("comp=userdelegationkey", StringComparison.Ordinal);
}

/// <summary>A token credential that hands out a fixed token: the offline stand-in for Microsoft Entra ID.</summary>
internal sealed class FakeCredential : TokenCredential
{
    public int Calls { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
