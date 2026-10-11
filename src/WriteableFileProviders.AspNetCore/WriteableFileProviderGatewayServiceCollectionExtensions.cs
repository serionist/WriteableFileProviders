using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace WriteableFileProviders.AspNetCore;

/// <summary>Registers the signed-URL gateway and the URL service.</summary>
public static class WriteableFileProviderGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway's services with its default options, served at <paramref name="publicBaseUri"/>: see
    /// <see cref="AddWriteableFileProviderGateway(IServiceCollection, Action{WriteableFileProviderGatewayOptions})"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="publicBaseUri">
    /// The absolute URL the gateway is reachable at from outside
    /// (<see cref="WriteableFileProviderGatewayOptions.PublicBaseUri"/>): <c>https</c>, or <c>http</c> on a loopback host.
    /// </param>
    public static IServiceCollection AddWriteableFileProviderGateway(this IServiceCollection services, Uri publicBaseUri)
    {
        ArgumentNullException.ThrowIfNull(publicBaseUri);
        return services.AddWriteableFileProviderGateway(options => options.PublicBaseUri = publicBaseUri);
    }

    /// <summary>
    /// Registers the gateway's services: <see cref="IWriteableFileUrlService"/>, the options (validated when the app
    /// starts), ASP.NET Core Data Protection for the tokens, and the default <see cref="IGatewayReplayStore"/> (in memory,
    /// one instance). Then map the endpoints with
    /// <see cref="WriteableFileProviderGatewayEndpointRouteBuilderExtensions.MapWriteableFileProviderGateway"/>, and
    /// register the provider as <see cref="IWriteableFileProvider"/> (<c>AddWriteableFileProvider</c>), or providers under
    /// keys (<c>AddKeyedSingleton&lt;IWriteableFileProvider&gt;</c>).
    /// </summary>
    /// <remarks>
    /// Tokens are protected with the app's Data Protection keys. An app on several instances shares its key ring
    /// (<c>PersistKeysTo...</c>, <c>ProtectKeysWith...</c>) and its application name, or a token issued by one instance
    /// does not redeem on another, and keys kept only in memory lose every issued URL on restart. A service registered
    /// before or after this call (<see cref="IGatewayReplayStore"/>, <see cref="TimeProvider"/>) replaces the default.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; <see cref="WriteableFileProviderGatewayOptions.PublicBaseUri"/> is required.</param>
    public static IServiceCollection AddWriteableFileProviderGateway(this IServiceCollection services, Action<WriteableFileProviderGatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<WriteableFileProviderGatewayOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WriteableFileProviderGatewayOptions>, WriteableFileProviderGatewayOptionsValidator>());
        services.TryAddSingleton<IGatewayReplayStore, InMemoryGatewayReplayStore>();
        services.TryAddSingleton<GatewayTokenCodec>();
        services.TryAddSingleton<GatewayRequestHandler>();
        services.TryAddSingleton<IWriteableFileUrlService, WriteableFileUrlService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpLoggingInterceptor, GatewayHttpLoggingInterceptor>());
        return services;
    }
}
