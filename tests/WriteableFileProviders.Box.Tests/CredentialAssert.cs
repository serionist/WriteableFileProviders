using System.Collections;
using System.Reflection;
using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Box.Tests.Fake;

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// Walks an exception, its inner exceptions and the Box SDK objects they hold, by reflection, and fails on anything that
/// would leak a credential: an <c>Authorization</c> header, a request body, an access token, the client secret or a JWT
/// assertion.
/// </summary>
internal static class CredentialAssert
{
    private static readonly string[] _forbidden = ["Bearer ", "token-", TestCredentials.Default.ClientSecret, "wrong-secret", "assertion", "client_secret"];

    public static void CarriesNone(Exception exception)
    {
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
                    foreach (var forbidden in _forbidden)
                        Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{where} contains '{forbidden}': {text}");
                    return;
                case IDictionary<string, string> map:
                    foreach (var (key, item) in map)
                    {
                        Assert.IsFalse(key.Equals("authorization", StringComparison.OrdinalIgnoreCase), $"{where} holds an Authorization header");
                        Walk(item, $"{where}[{key}]");
                    }
                    return;
                case IReadOnlyDictionary<string, string> map:
                    foreach (var (key, item) in map)
                    {
                        Assert.IsFalse(key.Equals("authorization", StringComparison.OrdinalIgnoreCase), $"{where} holds an Authorization header");
                        Walk(item, $"{where}[{key}]");
                    }
                    return;
            }
            if (!seen.Add(value))
                return;
            var type = value.GetType();
            if (value is RequestInfo request)
                Assert.IsNull(request.Body, $"{where}.Body holds the request body");
            if (value is BoxApiException)
                boxErrors++;
            if (value is IEnumerable sequence && value is not Exception)
            {
                foreach (var item in sequence)
                    Walk(item, where + "[]");
                return;
            }
            if (value is not Exception && type.Namespace?.StartsWith("Box.Sdk.Gen", StringComparison.Ordinal) != true)
                return;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.Name is nameof(Exception.TargetSite) or nameof(Exception.StackTrace))
                    continue;
                Walk(property.GetValue(value), $"{where}.{property.Name}");
            }
            Walk(value.ToString(), $"{where}.ToString()");
        }
    }
}
