using Microsoft.Extensions.Configuration;
using WriteableFileProviders;

// The .NET convention: registrations live in Microsoft.Extensions.DependencyInjection, so they need no using of their own.
#pragma warning disable IDE0130 // the namespace is the convention's, not this folder's
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

/// <summary>Registers a writeable file provider in a service collection, as a singleton <see cref="IWriteableFileProvider"/>.</summary>
/// <remarks>
/// Each call adds a registration, as <c>AddSingleton</c> does: injecting <see cref="IWriteableFileProvider"/> gives the
/// last one registered, and <c>GetServices&lt;IWriteableFileProvider&gt;()</c> gives every one. An app that uses several
/// providers tells them apart its own way: keyed services
/// (<c>AddKeyedSingleton&lt;IWriteableFileProvider&gt;(key, ...)</c>, injected with <c>[FromKeyedServices(key)]</c>),
/// <c>GetServices</c>, or interfaces of its own.
/// </remarks>
public static class WriteableFileProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers a provider built from a configuration section (its <c>Type</c> and its options' property names; see
    /// <see cref="WriteableFileProviderSerializer"/>), as a singleton <see cref="IWriteableFileProvider"/> that the
    /// container builds on first use and disposes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section that describes the provider.</param>
    public static IServiceCollection AddWriteableFileProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddWriteableFileProvider(_ => WriteableFileProviderSerializer.FromConfiguration(configuration));
    }

    /// <summary>
    /// Registers a provider the factory builds, as a singleton <see cref="IWriteableFileProvider"/> that the container
    /// builds on first use and disposes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Builds the provider.</param>
    public static IServiceCollection AddWriteableFileProvider(this IServiceCollection services, Func<IServiceProvider, IWriteableFileProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        return services.AddSingleton(factory);
    }
}
