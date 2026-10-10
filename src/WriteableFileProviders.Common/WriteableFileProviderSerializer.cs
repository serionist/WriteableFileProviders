using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace WriteableFileProviders;

/// <summary>
/// Builds a provider from configuration or JSON, and writes one back as JSON: the <c>Type</c> key holds the provider's
/// class name (matched case-insensitively), and the other keys are its options' property names (read
/// case-insensitively). This shape is what consumers store, so it never changes (AGENTS.md, one-way doors).
/// </summary>
/// <remarks>
/// <para>
/// A <c>Type</c> is resolved in this order: the assemblies passed in; the providers of this library, loading their
/// package's assembly when it is not loaded yet; then the types registered with <see cref="Register{TProvider}"/>. A name
/// two types share throws. Loaded assemblies are not searched, so a third-party provider is registered or passed in.
/// </para>
/// <para>
/// Values are read leniently, as the 2026 serializer read them: a number or a boolean into a string, the strings
/// <c>"true"</c>/<c>"false"</c> into a boolean, and a type that converts from a string (such as an SDK's constant class)
/// from a string or from an object holding it under <c>Value</c>. Such a type is written as a string.
/// </para>
/// </remarks>
public static class WriteableFileProviderSerializer
{
    private const string TypeKey = "Type";

    private static readonly Dictionary<string, string> _builtInProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WriteablePhysicalFileProvider"] = "WriteableFileProviders.Physical",
        ["WriteableAzureBlobFileProvider"] = "WriteableFileProviders.AzureBlob",
        ["WriteableAmazonS3FileProvider"] = "WriteableFileProviders.AmazonS3",
        ["WriteableMinioFileProvider"] = "WriteableFileProviders.Minio",
        ["WriteableGoogleCloudStorageFileProvider"] = "WriteableFileProviders.GoogleCloudStorage",
        ["WriteableBoxFileProvider"] = "WriteableFileProviders.Box",
        ["WriteableSharePointFileProvider"] = "WriteableFileProviders.SharePoint",
    };

    private static readonly ConcurrentDictionary<string, Type> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a provider type, so a configuration whose <c>Type</c> is its class name finds it.</summary>
    /// <typeparam name="TProvider">The provider type.</typeparam>
    /// <exception cref="ArgumentException">The type is abstract, or another type is registered under its class name.</exception>
    public static void Register<TProvider>() where TProvider : IWriteableFileProvider
    {
        var providerType = typeof(TProvider);
        if (!IsProvider(providerType))
            throw new ArgumentException($"{providerType.Name} is not a non-abstract class that implements IWriteableFileProvider.", nameof(TProvider));
        var registered = _registered.GetOrAdd(providerType.Name, providerType);
        if (registered != providerType)
            throw new ArgumentException($"FileProvider type name '{providerType.Name}' is already registered for {registered.FullName}.", nameof(TProvider));
    }

    /// <summary>Builds a provider from a configuration section: its <c>Type</c> and its options' property names.</summary>
    /// <param name="configSection">The section.</param>
    /// <param name="searchInAssemblies">Assemblies to search first for the provider type.</param>
    /// <exception cref="ArgumentException">The <c>Type</c> is missing, unknown or ambiguous, or a value cannot be converted.</exception>
    public static IWriteableFileProvider FromConfiguration(IConfiguration configSection, params Assembly[] searchInAssemblies)
    {
        ArgumentNullException.ThrowIfNull(configSection);
        var providerType = ResolveProviderType(configSection[TypeKey], searchInAssemblies);
        var optionsType = OptionsTypeOf(providerType);
        var options = Activator.CreateInstance(optionsType)!;
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.SetMethod!.IsPublic))
        {
            var section = configSection.GetSection(property.Name);
            if (property.PropertyType == typeof(string[]))
            {
                var items = section.GetChildren().Select(c => c.Value).OfType<string>().ToArray();
                if (items.Length > 0)
                    property.SetValue(options, items);
                continue;
            }
            if (section.Value is not { } value)
                continue;
            try
            {
                property.SetValue(options, OptionsJson.FromConfigurationString(value, property.PropertyType));
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException or TargetInvocationException)
            {
                throw new ArgumentException($"The configuration value of '{property.Name}' cannot be converted to {property.PropertyType.Name}.", nameof(configSection), e);
            }
        }
        return Create(providerType, optionsType, options);
    }

    /// <summary>Builds a provider from JSON text.</summary>
    /// <param name="json">The JSON: an object with <c>Type</c> and the options' properties.</param>
    /// <param name="searchInAssemblies">Assemblies to search first for the provider type.</param>
    /// <exception cref="ArgumentException">The <c>Type</c> is missing, unknown or ambiguous, or the JSON does not fit the options.</exception>
    public static IWriteableFileProvider FromJson(string json, params Assembly[] searchInAssemblies)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return FromJson(document.RootElement, searchInAssemblies);
    }

    /// <summary>Builds a provider from a JSON object.</summary>
    /// <param name="json">The object: <c>Type</c> and the options' properties.</param>
    /// <param name="searchInAssemblies">Assemblies to search first for the provider type.</param>
    /// <exception cref="ArgumentException">The <c>Type</c> is missing, unknown or ambiguous, or the JSON does not fit the options.</exception>
    public static IWriteableFileProvider FromJson(JsonObject json, params Assembly[] searchInAssemblies)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromJson(json.ToJsonString(), searchInAssemblies);
    }

    /// <summary>Builds a provider from a JSON element.</summary>
    /// <param name="json">The element: an object with <c>Type</c> and the options' properties.</param>
    /// <param name="searchInAssemblies">Assemblies to search first for the provider type.</param>
    /// <exception cref="ArgumentException">The <c>Type</c> is missing, unknown or ambiguous, or the JSON does not fit the options.</exception>
    public static IWriteableFileProvider FromJson(JsonElement json, params Assembly[] searchInAssemblies)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The JSON is not an object.", nameof(json));
        string? typeName = null;
        foreach (var property in json.EnumerateObject())
        {
            if (string.Equals(property.Name, TypeKey, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                typeName = property.Value.GetString();
        }
        var providerType = ResolveProviderType(typeName, searchInAssemblies);
        var optionsType = OptionsTypeOf(providerType);
        object options;
        try
        {
            options = json.Deserialize(optionsType, OptionsJson.Options)!;
        }
        catch (Exception e) when (e is JsonException or FormatException or TargetInvocationException)
        {
            throw new ArgumentException($"The JSON does not fit {optionsType.Name}: {e.Message}", nameof(json), e);
        }
        return Create(providerType, optionsType, options);
    }

    /// <summary>
    /// The provider as JSON: its options' properties and <c>Type</c>. Warning: it contains the options' secrets (keys,
    /// passwords, tokens) and must never be logged.
    /// </summary>
    /// <param name="provider">The provider.</param>
    public static JsonObject ToJson(IWriteableFileProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var json = JsonSerializer.SerializeToNode(provider.Options, provider.Options.GetType(), OptionsJson.Options)!.AsObject();
        json[TypeKey] = provider.GetType().Name;
        return json;
    }

    /// <summary>The provider as JSON text; see <see cref="ToJson"/>. It contains secrets: never log it.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="indented">Whether to indent the JSON.</param>
    public static string ToJsonString(IWriteableFileProvider provider, bool indented = false) =>
        ToJson(provider).ToJsonString(new JsonSerializerOptions(OptionsJson.Options) { WriteIndented = indented });

    /// <summary>The provider type a <c>Type</c> name stands for; see the remarks of <see cref="WriteableFileProviderSerializer"/>.</summary>
    /// <param name="typeName">The provider's class name.</param>
    /// <param name="searchInAssemblies">Assemblies to search first.</param>
    /// <exception cref="ArgumentException">The name is missing, unknown or ambiguous.</exception>
    internal static Type ResolveProviderType(string? typeName, params Assembly[] searchInAssemblies)
    {
        if (string.IsNullOrEmpty(typeName))
            throw new ArgumentException("FileProvider cannot be parsed. 'Type' is not provided", nameof(typeName));
        if (Find(typeName, searchInAssemblies ?? []) is { } given)
            return given;
        if (_builtInProviders.TryGetValue(typeName, out var assemblyName) && Load(assemblyName) is { } assembly && Find(typeName, [assembly]) is { } builtIn)
            return builtIn;
        return _registered.TryGetValue(typeName, out var registered)
            ? registered
            : throw new ArgumentException($"FileProvider cannot be parsed. Type: '{typeName}' is not recognized. Pass its assembly, or register it with WriteableFileProviderSerializer.Register<T>().", nameof(typeName));
    }

    private static Type? Find(string typeName, IEnumerable<Assembly> assemblies)
    {
        var matches = assemblies.Distinct().SelectMany(TypesOf)
            .Where(t => IsProvider(t) && string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new ArgumentException($"FileProvider cannot be parsed. Type: '{typeName}' is ambiguous: {string.Join(", ", matches.Select(m => m.FullName))}", nameof(typeName)),
        };
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    private static Assembly? Load(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsProvider(Type type) =>
        type is { IsClass: true, IsAbstract: false } && typeof(IWriteableFileProvider).IsAssignableFrom(type);

    private static Type OptionsTypeOf(Type providerType)
    {
        for (var type = providerType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(WriteableFileProvider<>))
                return type.GetGenericArguments()[0];
        }
        throw new ArgumentException($"No options type can be found for provider {providerType.Name}: it does not derive from WriteableFileProvider<TOptions>.");
    }

    private static IWriteableFileProvider Create(Type providerType, Type optionsType, object options)
    {
        var constructor = providerType.GetConstructor([optionsType])
            ?? throw new ArgumentException($"No Constructor can be found on FileProvider '{providerType.Name}' that accepts a single options object");
        try
        {
            return (IWriteableFileProvider)constructor.Invoke([options]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
