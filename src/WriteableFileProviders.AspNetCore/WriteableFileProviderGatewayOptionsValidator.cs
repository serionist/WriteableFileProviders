using Microsoft.Extensions.Options;

namespace WriteableFileProviders.AspNetCore;

/// <summary>Validates <see cref="WriteableFileProviderGatewayOptions"/>, every rule at once, when the app starts.</summary>
internal sealed class WriteableFileProviderGatewayOptionsValidator : IValidateOptions<WriteableFileProviderGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, WriteableFileProviderGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        ValidatePublicBaseUri(options, failures);
        if (!IsValidBasePath(options.BasePath))
            failures.Add($"BasePath '{options.BasePath}' must start with '/', not end with one, and hold only letters, digits, '-', '.', '_', '~' and '/' between non-empty segments that are not '.' or '..'.");
        ValidateValidity(nameof(options.MaxReadValidity), options.MaxReadValidity, failures);
        ValidateValidity(nameof(options.MaxUploadValidity), options.MaxUploadValidity, failures);
        if (options.MaxUploadBytes <= 0)
            failures.Add($"MaxUploadBytes must be positive; it is {options.MaxUploadBytes}.");
        if (string.IsNullOrWhiteSpace(options.DataProtectionPurpose))
            failures.Add($"DataProtectionPurpose must not be empty: it is the root Data Protection purpose of every gateway token ({GatewayTokenCodec.DefaultPurpose} by default).");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePublicBaseUri(WriteableFileProviderGatewayOptions options, List<string> failures)
    {
        if (options.PublicBaseUri is not { } uri)
        {
            failures.Add("PublicBaseUri is required: the absolute URL the app is reachable at, such as https://files.example.com/. Gateway URLs are never built from the request's Host header.");
            return;
        }
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"PublicBaseUri '{uri}' must be an absolute https URL.");
            return;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !options.AllowInsecureHttp)
            failures.Add($"PublicBaseUri '{uri}' is plain http on a host that is not loopback, which exposes every token on the way; use https, or set AllowInsecureHttp.");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            failures.Add($"PublicBaseUri '{uri}' cannot have a query, a fragment or user information.");
    }

    private static void ValidateValidity(string name, TimeSpan value, List<string> failures)
    {
        if (value <= TimeSpan.Zero || value > WriteableFileProviderGatewayOptions.MaxValidityLimit)
            failures.Add($"{name} must be positive and at most {WriteableFileProviderGatewayOptions.MaxValidityLimit}; it is {value}.");
    }

    private static bool IsValidBasePath(string? basePath)
    {
        if (basePath is not { Length: > 1 } || basePath[0] != '/' || basePath[^1] == '/')
            return false;
        foreach (var segment in basePath[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || !segment.All(IsUnreserved))
                return false;
        }
        return true;
    }

    private static bool IsUnreserved(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}
