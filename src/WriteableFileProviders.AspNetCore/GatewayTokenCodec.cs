using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace WriteableFileProviders.AspNetCore;

/// <summary>What a gateway token carries, encrypted and signed.</summary>
internal sealed record GatewayTokenPayload
{
    /// <summary>The file's path segments, relative to the provider root.</summary>
    [JsonPropertyName("p")]
    public required string[] Path { get; init; }

    /// <summary>What the token allows.</summary>
    [JsonPropertyName("a")]
    [JsonConverter(typeof(JsonStringEnumConverter<SignedUrlAccess>))]
    public required SignedUrlAccess Access { get; init; }

    /// <summary>A download's response content type, or the content type an upload must send and is stored with.</summary>
    [JsonPropertyName("t")]
    public string? ContentType { get; init; }

    /// <summary>The file name a download is saved as.</summary>
    [JsonPropertyName("f")]
    public string? FileName { get; init; }

    /// <summary>The token's unique id, for one-time use.</summary>
    [JsonPropertyName("j")]
    public required string Id { get; init; }
}

/// <summary>A token the gateway could read: the provider it names, its payload, and when it expires.</summary>
/// <param name="ProviderName">The keyed provider's name.</param>
/// <param name="Payload">The payload.</param>
/// <param name="ExpiresOn">When the token expires.</param>
internal sealed record GatewayToken(string ProviderName, GatewayTokenPayload Payload, DateTimeOffset ExpiresOn);

/// <summary>
/// Writes and reads gateway tokens. A token is <c>{access}.{provider}.{payload}</c>: a letter for the access (r, c, w),
/// the provider's name in base64url, and the payload, protected by a time-limited Data Protection protector whose purpose
/// chain is <see cref="Purpose"/>, the provider's name and the access. The first two parts are in the clear only to pick
/// that protector: changing either one changes the purpose, so the payload no longer unprotects.
/// </summary>
/// <remarks>The format and the purpose chain are a one-way door: tokens issued by one version redeem on the next.</remarks>
/// <param name="dataProtectionProvider">The app's Data Protection.</param>
internal sealed class GatewayTokenCodec(IDataProtectionProvider dataProtectionProvider)
{
    /// <summary>The root purpose of every gateway token.</summary>
    internal const string Purpose = "WriteableFileProviders.Gateway.v1";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Protects a payload for a provider until <paramref name="expiresOn"/>.</summary>
    /// <param name="providerName">The keyed provider's name.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="expiresOn">When the token stops working.</param>
    public string Protect(string providerName, GatewayTokenPayload payload, DateTimeOffset expiresOn)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var protectedPayload = Protector(providerName, payload.Access).Protect(json, expiresOn);
        return string.Join('.', AccessCode(payload.Access), WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(providerName)),
            WebEncoders.Base64UrlEncode(protectedPayload));
    }

    /// <summary>Reads a token: null when it is malformed, tampered with, expired, or protected for another purpose.</summary>
    /// <param name="token">The token from the URL.</param>
    public GatewayToken? Read(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 } || !TryParseAccess(parts[0], out var access))
            return null;
        string providerName;
        byte[] protectedPayload;
        try
        {
            providerName = _strictUtf8.GetString(WebEncoders.Base64UrlDecode(parts[1]));
            protectedPayload = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
        if (providerName.Length == 0 || protectedPayload.Length == 0)
            return null;

        byte[] json;
        DateTimeOffset expiresOn;
        try
        {
            json = Protector(providerName, access).Unprotect(protectedPayload, out expiresOn);
        }
        catch (CryptographicException)
        {
            return null;
        }

        GatewayTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GatewayTokenPayload>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        return payload is not null && payload.Access == access && payload.Path is not null
            && Array.TrueForAll(payload.Path, segment => segment is not null) && !string.IsNullOrEmpty(payload.Id)
            ? new GatewayToken(providerName, payload, expiresOn)
            : null;
    }

    private ITimeLimitedDataProtector Protector(string providerName, SignedUrlAccess access) =>
        dataProtectionProvider.CreateProtector(Purpose, providerName, access.ToString()).ToTimeLimitedDataProtector();

    private static string AccessCode(SignedUrlAccess access) => access switch
    {
        SignedUrlAccess.Read => "r",
        SignedUrlAccess.Create => "c",
        SignedUrlAccess.Write => "w",
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Unknown access."),
    };

    private static bool TryParseAccess(string code, out SignedUrlAccess access)
    {
        (var known, access) = code switch
        {
            "r" => (true, SignedUrlAccess.Read),
            "c" => (true, SignedUrlAccess.Create),
            "w" => (true, SignedUrlAccess.Write),
            _ => (false, default),
        };
        return known;
    }
}
