using System.Collections;
using System.Reflection;
using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// Walks an exception, its inner exceptions and everything they hold, by reflection: the Box SDK's objects, dictionaries
/// (<see cref="Exception.Data"/>, <c>Dictionary&lt;string, object&gt;</c> context values), sequences and every value's
/// text. It fails on anything that would leak a credential: an <c>Authorization</c> header, a request body, an access
/// token, the client secret, a JWT assertion, or a value the caller names.
/// </summary>
internal static class CredentialAssert
{
    private static readonly string[] _forbidden = ["Bearer ", "token-", TestCredentials.Default.ClientSecret, "wrong-secret", "assertion", "client_secret"];

    public static void CarriesNone(Exception exception, params string[] alsoForbidden)
    {
        var forbidden = _forbidden.Concat(alsoForbidden).ToArray();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var boxErrors = 0;
        Walk(exception, "exception");
        Assert.IsTrue(boxErrors > 0, "The exception graph holds no Box error to check");

        void Walk(object? value, string where)
        {
            switch (value)
            {
                case null:
                    return;
                case string text:
                    foreach (var word in forbidden)
                        Assert.IsFalse(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{where} contains '{word}': {text}");
                    return;
            }
            var type = value.GetType();
            if (!type.IsValueType && !seen.Add(value))
                return;
            switch (value)
            {
                case IDictionary map:
                    foreach (DictionaryEntry entry in map)
                    {
                        Assert.IsFalse(string.Equals(entry.Key as string, "authorization", StringComparison.OrdinalIgnoreCase), $"{where} holds an Authorization header");
                        Walk(entry.Key, $"{where}.key");
                        Walk(entry.Value, $"{where}[{entry.Key}]");
                    }
                    return;
                case IReadOnlyDictionary<string, string> map:
                    foreach (var (key, item) in map)
                    {
                        Assert.IsFalse(key.Equals("authorization", StringComparison.OrdinalIgnoreCase), $"{where} holds an Authorization header");
                        Walk(key, $"{where}.key");
                        Walk(item, $"{where}[{key}]");
                    }
                    return;
                case RequestInfo request:
                    Assert.IsNull(request.Body, $"{where}.Body holds the request body");
                    break;
                case BoxApiException:
                    boxErrors++;
                    break;
            }
            if (value is IEnumerable sequence && value is not Exception)
            {
                foreach (var item in sequence)
                    Walk(item, where + "[]");
                return;
            }
            if (value is Exception || type.Namespace?.StartsWith("Box.Sdk.Gen", StringComparison.Ordinal) == true)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 || property.Name is nameof(Exception.TargetSite) or nameof(Exception.StackTrace))
                        continue;
                    Walk(property.GetValue(value), $"{where}.{property.Name}");
                }
            }
            // Every value's text too: a JsonElement of the error's context, a number, the exception's own ToString().
            Walk(value.ToString(), $"{where}.ToString()");
        }
    }
}
