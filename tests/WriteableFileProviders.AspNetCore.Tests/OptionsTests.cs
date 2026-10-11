using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>The gateway's defaults (D22) and the validation of its options.</summary>
[TestClass]
public sealed class OptionsTests
{
    private static readonly WriteableFileProviderGatewayOptionsValidator _validator = new();

    [TestMethod]
    public void Defaults_FailClosed()
    {
        var options = new WriteableFileProviderGatewayOptions();

        Assert.IsNull(options.PublicBaseUri);
        Assert.AreEqual("/files", options.BasePath);
        Assert.AreEqual(TimeSpan.FromDays(7), options.MaxReadValidity);
        Assert.AreEqual(TimeSpan.FromHours(1), options.MaxUploadValidity);
        Assert.IsTrue(options.OneTimeUploadTokens);
        Assert.AreEqual(100L * 1024 * 1024, options.MaxUploadBytes);
        Assert.IsFalse(options.PreferGateway);
        Assert.IsFalse(options.AllowInsecureHttp);
        Assert.AreEqual(TimeSpan.FromDays(7), WriteableFileProviderGatewayOptions.MaxValidityLimit);
    }

    [TestMethod]
    [DataRow("https://files.example.com/")]
    [DataRow("https://files.example.com/app")]
    [DataRow("https://files.example.com:8443/app/")]
    [DataRow("http://localhost:5000/")]
    [DataRow("http://127.0.0.1/")]
    [DataRow("http://[::1]/")]
    public void PublicBaseUri_Valid(string uri) => AssertValid(Valid() with { PublicBaseUri = new Uri(uri) });

    [TestMethod]
    [DataRow("http://files.example.com/", "plain http")]
    [DataRow("ftp://files.example.com/", "absolute https")]
    [DataRow("file:///c:/files", "absolute https")]
    [DataRow("https://files.example.com/?x=1", "query")]
    [DataRow("https://files.example.com/#top", "query")]
    [DataRow("https://user@files.example.com/", "query")]
    public void PublicBaseUri_Invalid(string uri, string message) =>
        AssertInvalid(Valid() with { PublicBaseUri = new Uri(uri) }, message);

    [TestMethod]
    public void PublicBaseUri_Relative_IsInvalid() =>
        AssertInvalid(Valid() with { PublicBaseUri = new Uri("/files", UriKind.Relative) }, "absolute https");

    [TestMethod]
    public void PublicBaseUri_Missing_IsInvalid() => AssertInvalid(Valid() with { PublicBaseUri = null }, "PublicBaseUri is required");

    [TestMethod]
    public void PublicBaseUri_PlainHttp_WithAllowInsecureHttp_IsValid() =>
        AssertValid(Valid() with { PublicBaseUri = new Uri("http://files.internal/"), AllowInsecureHttp = true });

    [TestMethod]
    [DataRow("/files")]
    [DataRow("/api/v1/files")]
    [DataRow("/_f~.-9")]
    public void BasePath_Valid(string basePath) => AssertValid(Valid() with { BasePath = basePath });

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("/")]
    [DataRow("files")]
    [DataRow("/files/")]
    [DataRow("/a//b")]
    [DataRow("/a/./b")]
    [DataRow("/a/../b")]
    [DataRow("/fi les")]
    [DataRow("/files/{token}")]
    [DataRow("/files?x")]
    [DataRow("/fïles")]
    public void BasePath_Invalid(string? basePath) => AssertInvalid(Valid() with { BasePath = basePath! }, "BasePath");

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(7.001)]
    public void Validities_OutOfRange_AreInvalid(double days)
    {
        AssertInvalid(Valid() with { MaxReadValidity = TimeSpan.FromDays(days) }, "MaxReadValidity");
        AssertInvalid(Valid() with { MaxUploadValidity = TimeSpan.FromDays(days) }, "MaxUploadValidity");
    }

    [TestMethod]
    public void Validities_UpToSevenDays_AreValid() =>
        AssertValid(Valid() with { MaxReadValidity = TimeSpan.FromDays(7), MaxUploadValidity = TimeSpan.FromDays(7) });

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void MaxUploadBytes_NotPositive_IsInvalid(long bytes) => AssertInvalid(Valid() with { MaxUploadBytes = bytes }, "MaxUploadBytes");

    [TestMethod]
    public void EveryFailure_IsReportedAtOnce()
    {
        var result = _validator.Validate(null, new WriteableFileProviderGatewayOptions { BasePath = "nope", MaxUploadBytes = 0 });

        Assert.IsTrue(result.Failed);
        Assert.AreEqual(3, result.Failures!.Count());
    }

    [TestMethod]
    public void Validate_NullOptions_Throws() => Assert.ThrowsExactly<ArgumentNullException>(() => _validator.Validate(null, null!));

    [TestMethod]
    public async Task InvalidOptions_FailTheMapping()
    {
        var builder = Builder();
        builder.Services.AddWriteableFileProviderGateway(_ => { });
        await using var app = builder.Build();

        var failure = Assert.ThrowsExactly<OptionsValidationException>(() => app.MapWriteableFileProviderGateway());
        StringAssert.Contains(failure.Message, "PublicBaseUri is required");
    }

    [TestMethod]
    public async Task InvalidOptions_FailTheStart_WithoutTheGatewayMapped()
    {
        var builder = Builder();
        builder.Services.AddWriteableFileProviderGateway(options => options.PublicBaseUri = new Uri("http://files.example.com/"));
        await using var app = builder.Build();

        var failure = await Assert.ThrowsExactlyAsync<OptionsValidationException>(() => app.StartAsync());
        StringAssert.Contains(failure.Message, "plain http");
    }

    internal static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        return builder;
    }

    private static Settings Valid() => new() { PublicBaseUri = new Uri("https://files.example.com/") };

    private static void AssertValid(Settings settings)
    {
        var result = _validator.Validate(null, settings.ToOptions());
        Assert.IsTrue(result.Succeeded, result.FailureMessage);
    }

    private static void AssertInvalid(Settings settings, string message)
    {
        var result = _validator.Validate(null, settings.ToOptions());
        Assert.IsTrue(result.Failed);
        Assert.AreEqual(1, result.Failures!.Count(), result.FailureMessage);
        StringAssert.Contains(result.FailureMessage, message);
    }

    /// <summary>The options as a record, so each test changes one setting of a valid set.</summary>
    private sealed record Settings
    {
        public Uri? PublicBaseUri { get; init; }

        public string BasePath { get; init; } = "/files";

        public TimeSpan MaxReadValidity { get; init; } = TimeSpan.FromDays(7);

        public TimeSpan MaxUploadValidity { get; init; } = TimeSpan.FromHours(1);

        public long MaxUploadBytes { get; init; } = 1024;

        public bool AllowInsecureHttp { get; init; }

        public WriteableFileProviderGatewayOptions ToOptions() => new()
        {
            PublicBaseUri = PublicBaseUri,
            BasePath = BasePath,
            MaxReadValidity = MaxReadValidity,
            MaxUploadValidity = MaxUploadValidity,
            MaxUploadBytes = MaxUploadBytes,
            AllowInsecureHttp = AllowInsecureHttp,
        };
    }
}
