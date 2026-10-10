using System.Diagnostics.CodeAnalysis;

namespace WriteableFileProviders;

/// <summary>
/// The options every provider shares: the root folder the provider is confined to. Each provider's options class derives
/// from this one, and its property names are the configuration shape consumers store (AGENTS.md, one-way doors).
/// </summary>
public class FileProviderOptions
{
    private string[]? _rootDirectorySegments;

    /// <summary>
    /// The root folder as a path, such as <c>tenants/acme</c>: every path the provider takes is relative to it, and nothing
    /// outside it can be reached. Reading it gives the segments joined with <c>/</c> and a trailing <c>/</c>.
    /// </summary>
    public virtual string? RootDirectoryPath
    {
        get => string.Join('/', RootDirectorySegments) + "/";
        set => RootDirectorySegments = value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The root folder as segments; empty when the provider works at the store's root. Null sets it empty.</summary>
    [AllowNull]
    public string[] RootDirectorySegments
    {
        get => _rootDirectorySegments ?? [];
        set => _rootDirectorySegments = value;
    }
}
