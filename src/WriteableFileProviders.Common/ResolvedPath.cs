namespace WriteableFileProviders;

/// <summary>
/// A path a provider has validated and placed under its root: what <see cref="WriteableFileProvider{TOptions}"/> hands
/// its <c>...CoreAsync</c> members. Only the base class creates one from a caller's path; a provider may derive child
/// paths for the entries it lists.
/// </summary>
public sealed class ResolvedPath
{
    private readonly string[] _rootSegments;
    private readonly string[] _relativeSegments;

    internal ResolvedPath(string[] rootSegments, string[] relativeSegments)
    {
        _rootSegments = rootSegments;
        _relativeSegments = relativeSegments;
        Path = new ProviderPath(relativeSegments);
    }

    /// <summary>The path relative to the provider root, its segments validated.</summary>
    public ProviderPath Path { get; }

    /// <summary>The root's segments followed by the path's: what the store addresses.</summary>
    public string[] AbsoluteSegments => [.. _rootSegments, .. _relativeSegments];

    /// <summary>The path's segments, relative to the root.</summary>
    public string[] RelativeSegments => [.. _relativeSegments];

    /// <summary>Whether this is the provider root.</summary>
    public bool IsRoot => _relativeSegments.Length == 0;

    /// <summary>The last segment, or empty for the root.</summary>
    public string Name => Path.Name;

    /// <summary>The parent folder; the root's parent is the root.</summary>
    public ResolvedPath Parent => IsRoot ? this : new ResolvedPath(_rootSegments, _relativeSegments[..^1]);

    /// <summary>A child entry of this folder, named as the store names it.</summary>
    /// <param name="name">The child's name: one segment, as listed by the store.</param>
    public ResolvedPath Child(string name) => new(_rootSegments, [.. _relativeSegments, name]);

    /// <summary>The relative path joined with <c>/</c>.</summary>
    public override string ToString() => Path.ToString();
}
