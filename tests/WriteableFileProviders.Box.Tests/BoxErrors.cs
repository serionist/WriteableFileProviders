using Box.Sdk.Gen;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Box.Tests;

/// <summary>
/// The provider throws only the contract's exceptions; Box's own error travels inside, as the inner exception, where
/// these tests read its status and code.
/// </summary>
internal static class BoxErrors
{
    /// <summary>The Box error inside an exception the provider threw.</summary>
    /// <param name="exception">What the provider threw.</param>
    public static BoxApiException Inner(Exception exception)
    {
        Assert.IsInstanceOfType<BoxApiException>(exception.InnerException);
        return (BoxApiException)exception.InnerException!;
    }
}
