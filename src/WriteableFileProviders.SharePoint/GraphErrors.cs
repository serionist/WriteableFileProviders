using System.Text.Json;
using Azure.Identity;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace WriteableFileProviders.SharePoint;

/// <summary>
/// What a Graph call threw, in the contract's terms: only a 404 means "missing", and every other failure keeps
/// its status, its code and the SDK's exception as the inner exception.
/// </summary>
internal static class GraphErrors
{
    /// <summary>The error mapping the generated request builders use: every error body is an <see cref="ODataError"/>.</summary>
    public static Dictionary<string, ParsableFactory<IParsable>> Mapping { get; } = new() { ["XXX"] = ODataError.CreateFromDiscriminatorValue };

    /// <summary>The HTTP status of a failed Graph call, if it reached the service.</summary>
    public static int? Status(Exception exception) => exception is ApiException api ? api.ResponseStatusCode : null;

    /// <summary>The Graph error code (<c>itemNotFound</c>, <c>nameAlreadyExists</c>, ...), if the error had one.</summary>
    public static string? Code(Exception exception) => (exception as ODataError)?.Error?.Code;

    /// <summary>Whether the call found nothing at the path: a 404.</summary>
    public static bool IsNotFound(Exception exception) => Status(exception) == 404;

    /// <summary>
    /// Whether the exception came from the SDK or the service and must be mapped: an error answer, retries that ran out
    /// (<see cref="AggregateException"/>), a network failure, no answer within the HTTP client's timeout (a
    /// <see cref="TaskCanceledException"/> holding a <see cref="TimeoutException"/>), or an answer the SDK could not read
    /// (Kiota 2.0 raises <see cref="InvalidOperationException"/> for an error body that is not JSON, and
    /// <see cref="JsonException"/> for a JSON body that is malformed), or a sign-in that found no credential to use
    /// (<see cref="CredentialUnavailableException"/>, which Azure.Identity raises inside the request). Cancellation and
    /// disposal pass through.
    /// </summary>
    public static bool IsFailure(Exception exception) => exception switch
    {
        ApiException or AggregateException or HttpRequestException or JsonException or CredentialUnavailableException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        ObjectDisposedException => false,
        InvalidOperationException => true,
        _ => false,
    };

    /// <summary>An exception for a failed call: <see cref="UnauthorizedAccessException"/> for 401 and 403,
    /// <see cref="PreconditionFailedException"/> for 412, <see cref="InvalidOperationException"/> for a sign-in that found no
    /// credential (the store is not set up as configured), otherwise <see cref="IOException"/>.</summary>
    /// <param name="exception">What the SDK threw.</param>
    /// <param name="what">What the provider was doing, such as "Writing 'a/b.txt'".</param>
    /// <param name="givenClient">Whether the caller gave the provider its Graph client, and so its sign-in.</param>
    public static Exception Wrap(Exception exception, string what, bool givenClient = false)
    {
        switch (exception)
        {
            case CredentialUnavailableException:
                return new InvalidOperationException(givenClient
                    ? $"{what} failed: the Microsoft Entra ID credential of the given client found no credential to use."
                    : $"{what} failed: UseDefaultAzureCredential is set, but the Microsoft Entra ID sign-in found no credential to use (environment, workload or managed identity, developer sign-in). Configure one, or set ClientSecret or a certificate.",
                    exception);
            case AggregateException aggregate:
                var statuses = string.Join(", ", aggregate.Flatten().InnerExceptions.Select(Status).OfType<int>().Distinct());
                return new IOException($"{what} failed: Microsoft Graph kept answering {(statuses.Length == 0 ? "with errors" : statuses)} after its retries (throttled or unavailable).", exception);
            case ApiException api:
                var detail = Describe(api);
                return api.ResponseStatusCode switch
                {
                    401 or 403 => new UnauthorizedAccessException($"{what} was refused: {detail}.", exception),
                    412 => new PreconditionFailedException($"{what} failed: the file changed ({detail}).", exception),
                    _ => new IOException($"{what} failed: {detail}.", exception),
                };
            case HttpRequestException:
                return new IOException($"{what} failed: {exception.Message}", exception);
            case TaskCanceledException:
                return new IOException($"{what} failed: the service did not answer within the HTTP client's timeout.", exception);
            default:
                return new IOException($"{what} failed: Microsoft Graph answered something the SDK could not read.", exception);
        }
    }

    /// <summary>An exception for a failed answer the provider read itself (upload fragments, downloads, the copy monitor).</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="body">The answer's body, which may hold a Graph error.</param>
    /// <param name="what">What the provider was doing.</param>
    public static Exception FromResponse(int status, string? body, string what)
    {
        var code = CodeOf(body);
        var detail = code is null ? $"{status}" : $"{status} {code}";
        return status switch
        {
            401 or 403 => new UnauthorizedAccessException($"{what} was refused: {detail}."),
            412 => new PreconditionFailedException($"{what} failed: the file changed ({detail})."),
            _ => new IOException($"{what} failed: {detail}."),
        };
    }

    /// <summary>The <c>error.code</c> of a Graph error body, or null.</summary>
    /// <param name="body">The body.</param>
    public static string? CodeOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(ApiException exception)
    {
        var status = exception.ResponseStatusCode;
        if (exception is not ODataError { Error: { } error })
            return $"{status}";
        var inner = error.InnerError?.AdditionalData is { } data && data.TryGetValue("code", out var innerCode) ? $" ({innerCode})" : "";
        return $"{status} {error.Code}{inner}: {error.Message}";
    }
}
