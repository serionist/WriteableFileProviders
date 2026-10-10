using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WriteableFileProviders;

/// <summary>Registers writeable file providers in a service collection, as keyed singletons.</summary>
public static class WriteableFileProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers a provider built from a configuration section (its <c>Type</c> and its options' property names; see
    /// <see cref="WriteableFileProviderSerializer"/>), as a singleton keyed by <paramref name="name"/>. Resolve it with
    /// <c>GetRequiredKeyedService&lt;IWriteableFileProvider&gt;(name)</c> or <c>[FromKeyedServices(name)]</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The key.</param>
    /// <param name="configuration">The section that describes the provider.</param>
    public static IServiceCollection AddWriteableFileProvider(this IServiceCollection services, string name, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddWriteableFileProvider(name, _ => WriteableFileProviderSerializer.FromConfiguration(configuration));
    }

    /// <summary>Registers a provider the factory builds, as a singleton keyed by <paramref name="name"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The key.</param>
    /// <param name="factory">Builds the provider; the container disposes it.</param>
    public static IServiceCollection AddWriteableFileProvider(this IServiceCollection services, string name, Func<IServiceProvider, IWriteableFileProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        return services.AddKeyedSingleton(name, (serviceProvider, _) => factory(serviceProvider));
    }
}
