using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.S3.Testing;

/// <summary>
/// The account of a live suite: environment variables first, then an optional <c>testsettings.json</c> next to the test
/// assembly (git-ignored). A live suite runs only with <c>WFP_LIVE=1</c> and its settings; otherwise its tests are
/// inconclusive.
/// </summary>
internal static class LiveSettings
{
    private static readonly IConfiguration _file = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "testsettings.json"), optional: true)
        .Build();

    /// <summary>A setting, or null when it is not set.</summary>
    /// <param name="name">Its name, the same in the environment and in testsettings.json.</param>
    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment ? fromEnvironment
        : _file[name] is { Length: > 0 } fromFile ? fromFile
        : null;

    /// <summary>Makes the test inconclusive unless live runs are on and every named setting is present.</summary>
    /// <param name="names">The settings the suite needs.</param>
    public static void Require(params string[] names)
    {
        if (Environment.GetEnvironmentVariable("WFP_LIVE") != "1")
            Assert.Inconclusive("Live suites run only with WFP_LIVE=1, against an account you own.");
        var missing = names.Where(n => Get(n) is null).ToList();
        if (missing.Count > 0)
            Assert.Inconclusive($"Set {string.Join(", ", missing)} in the environment or in testsettings.json.");
    }
}
