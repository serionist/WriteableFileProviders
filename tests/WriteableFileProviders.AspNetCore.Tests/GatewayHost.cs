using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>
/// An app on TestServer with the gateway mapped and in-memory providers sharing one store under different roots: three
/// the app registers under keys itself (<c>AddKeyedSingleton</c>), of which <c>native</c> signs its own URLs and
/// <c>files</c> and <c>other</c> sign none, so the URL service falls back to the gateway; and, unless left out, the app's
/// default provider (<c>AddWriteableFileProvider</c>), which signs none either. An <c>/issue</c> endpoint issues a URL
/// from inside a request, for the Host header test.
/// </summary>
internal sealed class GatewayHost : IAsyncDisposable
{
    public static readonly Uri PublicBaseUri = new("https://files.example.com/");

    private readonly string _store;

    private GatewayHost(WebApplication app, string store)
    {
        App = app;
        _store = store;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => App.Services;

    public IWriteableFileUrlService Urls => Services.GetRequiredService<IWriteableFileUrlService>();

    /// <summary>The provider registered under <paramref name="name"/>, or the default provider for null.</summary>
    public IWriteableFileProvider Provider(string? name = "files") =>
        name is null ? Services.GetRequiredService<IWriteableFileProvider>() : Services.GetRequiredKeyedService<IWriteableFileProvider>(name);

    /// <summary>The <c>files</c> provider, which counts the gateway's reads.</summary>
    public DelegatingFileProvider Files => (DelegatingFileProvider)Provider();

    public static async Task<GatewayHost> StartAsync(
        Action<WriteableFileProviderGatewayOptions>? configure = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null,
        bool canPin = true,
        bool withDefault = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // Keys in memory: nothing written to the user profile.
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var store = "gateway-" + Guid.NewGuid().ToString("N");
        builder.Services.AddKeyedSingleton<IWriteableFileProvider>("native", (_, _) => InMemory(store, "native-root"));
        builder.Services.AddKeyedSingleton<IWriteableFileProvider>("files", (_, _) => new DelegatingFileProvider(InMemory(store, "files-root")) { CanPin = canPin });
        builder.Services.AddKeyedSingleton<IWriteableFileProvider>("other", (_, _) => new DelegatingFileProvider(InMemory(store, "other-root")));
        if (withDefault)
            builder.Services.AddWriteableFileProvider(_ => new DelegatingFileProvider(InMemory(store, "default-root")));
        builder.Services.AddWriteableFileProviderGateway(options =>
        {
            options.PublicBaseUri = PublicBaseUri;
            configure?.Invoke(options);
        });
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapWriteableFileProviderGateway();
        app.MapGet("/issue", async (HttpContext context, IWriteableFileUrlService urls, string provider, string path, SignedUrlAccess access) =>
            (await urls.GetSignedUrlAsync(path, new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Access = access }, provider, context.RequestAborted)).Url.ToString());
        await app.StartAsync();
        return new GatewayHost(app, store);
    }

    public async Task WriteAsync(string path, string text, string? provider = "files", string? contentType = null) =>
        await Provider(provider).UpdateFileAsync(path, TestStreams.Text(text), new UpdateFileOptions { ContentType = contentType });

    public async Task<string> ReadAsync(string path, string? provider = "files")
    {
        await using var stream = await Provider(provider).ReadFileAsync(path);
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
    }

    /// <summary>A URL for a file of the provider registered under <paramref name="provider"/>, or of the default provider for null.</summary>
    public async Task<Uri> UrlAsync(string path, SignedUrlAccess access = SignedUrlAccess.Read, string? provider = "files", string? fileName = null, string? contentType = null)
    {
        var options = new SignedUrlOptions
        {
            Validity = TimeSpan.FromMinutes(5),
            Access = access,
            DownloadFileName = fileName,
            ContentType = contentType,
        };
        var url = provider is null ? await Urls.GetSignedUrlAsync(path, options) : await Urls.GetSignedUrlAsync(path, options, provider);
        return url.Url;
    }

    /// <summary>The token segment of a gateway URL under <c>/files</c>, whatever path base comes before it.</summary>
    public static string TokenOf(Uri url) => url.Segments[Array.IndexOf(url.Segments, "files/") + 1].TrimEnd('/');

    /// <summary>A gateway URL with the given token.</summary>
    public static Uri GatewayUrl(string token) => new(PublicBaseUri, $"files/{token}/x");

    public Task<HttpResponseMessage> PutAsync(Uri url, string body, string? contentType = null)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (contentType is not null)
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return Client.PutAsync(url, content);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        InMemoryFileProvider.ClearStore(_store);
    }

    private static InMemoryFileProvider InMemory(string store, string root) =>
        new(new InMemoryFileProviderOptions { StoreName = store, RootDirectoryPath = root });
}
