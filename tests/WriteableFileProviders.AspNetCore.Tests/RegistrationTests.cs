using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>What <c>AddWriteableFileProviderGateway</c> and <c>MapWriteableFileProviderGateway</c> register.</summary>
[TestClass]
public sealed class RegistrationTests
{
    [TestMethod]
    public void Add_RegistersTheServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWriteableFileProviderGateway(options => options.PublicBaseUri = GatewayHost.PublicBaseUri);
        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<WriteableFileUrlService>(provider.GetRequiredService<IWriteableFileUrlService>());
        Assert.IsInstanceOfType<InMemoryGatewayReplayStore>(provider.GetRequiredService<IGatewayReplayStore>());
        Assert.AreSame(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.IsNotNull(provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
        Assert.AreSame(provider.GetRequiredService<IWriteableFileUrlService>(), provider.GetRequiredService<IWriteableFileUrlService>());
        Assert.AreEqual(GatewayHost.PublicBaseUri, provider.GetRequiredService<IOptions<WriteableFileProviderGatewayOptions>>().Value.PublicBaseUri);
    }

    [TestMethod]
    public void Add_WithAPublicBaseUri_RegistersTheServices_WithTheDefaultOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWriteableFileProviderGateway(new Uri("https://localhost:5001"));
        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<WriteableFileUrlService>(provider.GetRequiredService<IWriteableFileUrlService>());
        var options = provider.GetRequiredService<IOptions<WriteableFileProviderGatewayOptions>>().Value;
        Assert.AreEqual(new Uri("https://localhost:5001"), options.PublicBaseUri);
        Assert.AreEqual(new WriteableFileProviderGatewayOptions().BasePath, options.BasePath);
        Assert.AreEqual(new WriteableFileProviderGatewayOptions().MaxUploadBytes, options.MaxUploadBytes);
    }

    [TestMethod]
    public void Add_Twice_RegistersEachServiceOnce_AndAppliesBothConfigurations()
    {
        var services = new ServiceCollection();
        services.AddWriteableFileProviderGateway(options => options.PublicBaseUri = GatewayHost.PublicBaseUri);
        services.AddWriteableFileProviderGateway(options => options.MaxUploadBytes = 7);
        using var provider = services.BuildServiceProvider();

        Assert.AreEqual(1, provider.GetServices<IValidateOptions<WriteableFileProviderGatewayOptions>>().OfType<WriteableFileProviderGatewayOptionsValidator>().Count());
        Assert.AreEqual(1, provider.GetServices<IHttpLoggingInterceptor>().Count());
        Assert.AreEqual(1, provider.GetServices<IWriteableFileUrlService>().Count());
        var options = provider.GetRequiredService<IOptions<WriteableFileProviderGatewayOptions>>().Value;
        Assert.AreEqual(GatewayHost.PublicBaseUri, options.PublicBaseUri);
        Assert.AreEqual(7, options.MaxUploadBytes);
    }

    [TestMethod]
    public void ServicesRegisteredBefore_ReplaceTheDefaults()
    {
        var store = new RecordingReplayStore();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection();
        services.AddSingleton<IGatewayReplayStore>(store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddWriteableFileProviderGateway(options => options.PublicBaseUri = GatewayHost.PublicBaseUri);
        using var provider = services.BuildServiceProvider();

        Assert.AreSame(store, provider.GetRequiredService<IGatewayReplayStore>());
        Assert.AreSame(clock, provider.GetRequiredService<TimeProvider>());
    }

    [TestMethod]
    public async Task ReplayStoreRegisteredAfter_IsTheOneTheGatewayUses()
    {
        var store = new RecordingReplayStore();
        await using var host = await GatewayHost.StartAsync(configureBuilder: builder => builder.Services.AddSingleton<IGatewayReplayStore>(store));
        var url = await host.UrlAsync("doc.txt", SignedUrlAccess.Write);

        using var response = await host.PutAsync(url, "x");

        Assert.AreEqual(1, store.Calls);
    }

    [TestMethod]
    public void Add_NullArguments_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderGatewayServiceCollectionExtensions.AddWriteableFileProviderGateway(null!, _ => { }));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ServiceCollection().AddWriteableFileProviderGateway((Action<WriteableFileProviderGatewayOptions>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ServiceCollection().AddWriteableFileProviderGateway((Uri)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderGatewayServiceCollectionExtensions.AddWriteableFileProviderGateway(null!, GatewayHost.PublicBaseUri));
    }

    [TestMethod]
    public async Task Map_WithoutAdd_Throws()
    {
        await using var app = OptionsTests.Builder().Build();

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => app.MapWriteableFileProviderGateway());
        StringAssert.Contains(failure.Message, "AddWriteableFileProviderGateway");
    }

    [TestMethod]
    public void Map_Null_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => WriteableFileProviderGatewayEndpointRouteBuilderExtensions.MapWriteableFileProviderGateway(null!));

    [TestMethod]
    public async Task Map_AddsGetHeadAndPut_UnderTheBasePath()
    {
        await using var host = await GatewayHost.StartAsync(options => options.BasePath = "/dl");
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/dl", StringComparison.Ordinal) == true).ToList();

        Assert.AreEqual(2, endpoints.Count);
        var methods = endpoints.SelectMany(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods).Order().ToList();
        CollectionAssert.AreEqual(new[] { "GET", "HEAD", "PUT" }, methods);
        Assert.IsTrue(endpoints.All(e => e.RoutePattern.RawText == "/dl/{token}/{fileName?}"));
        Assert.IsTrue(endpoints.All(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null));
    }

    [TestMethod]
    public async Task UrlService_ForAProviderOfTheReferenceSuite_ServesThroughTheGateway()
    {
        // The in-memory reference provider, configured as Box and SharePoint behave (a fixed URL lifetime): a shorter
        // validity than its lifetime is not supported natively, so the gateway issues the URL.
        var store = "fixed-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var host = await GatewayHost.StartAsync(configureBuilder: builder =>
                builder.Services.AddKeyedSingleton<IWriteableFileProvider>("fixed", (_, _) => new InMemoryFileProvider(new InMemoryFileProviderOptions
                {
                    StoreName = store,
                    FixedSignedUrlLifetime = TimeSpan.FromHours(1),
                })));
            await host.WriteAsync("doc.txt", "fixed lifetime", provider: "fixed");

            var shorter = await host.Urls.GetSignedUrlAsync("doc.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5) }, "fixed");
            var longer = await host.Urls.GetSignedUrlAsync("doc.txt", new SignedUrlOptions { Validity = TimeSpan.FromHours(2), AllowShorterValidity = true }, "fixed");

            Assert.AreEqual(SignedUrlKind.Gateway, shorter.Kind);
            Assert.AreEqual(SignedUrlKind.Presigned, longer.Kind);
            using var response = await host.Client.GetAsync(shorter.Url);
            Assert.AreEqual("fixed lifetime", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            InMemoryFileProvider.ClearStore(store);
        }
    }

    private sealed class RecordingReplayStore : IGatewayReplayStore
    {
        public int Calls { get; private set; }

        public Task<bool> TryConsumeAsync(string tokenId, DateTimeOffset expiresOn, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }
}
