namespace WriteableFileProviders;

/// <summary>
/// A path inside a provider, relative to the provider root: a list of segments such as <c>["reports", "2026.csv"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A string converts to a path by splitting it on <c>/</c> and <c>\</c> and dropping empty parts, so <c>"reports/2026.csv"</c>,
/// <c>"/reports//2026.csv"</c> and <c>@"reports\2026.csv"</c> are the same path. An array of segments converts as it is: a
/// segment may still contain separators, and an empty segment stays, so the provider refuses it.
/// </para>
/// <para>
/// A path is not validated when it is built. Every provider validates it when it is used: a segment that is empty, only
/// dots and spaces, holds a control character, or turns into one of those when percent-decoded is refused with an
/// <see cref="ArgumentException"/>. The default path is the provider root.
/// </para>
/// </remarks>
public readonly struct ProviderPath : IEquatable<ProviderPath>
{
    private static readonly char[] _separators = ['/', '\\'];
    private readonly string[]? _segments;

    /// <summary>Creates a path from its segments, as they are.</summary>
    /// <param name="segments">The segments, relative to the provider root. Null or empty is the root.</param>
    public ProviderPath(params string[]? segments) => _segments = segments is { Length: > 0 } ? [.. segments] : null;

    /// <summary>The provider root: a path with no segments.</summary>
    public static ProviderPath Root => default;

    /// <summary>The segments of the path.</summary>
    public IReadOnlyList<string> Segments => _segments ?? [];

    /// <summary>Whether this is the provider root (no segments).</summary>
    public bool IsRoot => _segments is null;

    /// <summary>The last segment, or an empty string for the root.</summary>
    public string Name => _segments is { } s ? s[^1] : "";

    /// <summary>The path without its last segment; the root's parent is the root.</summary>
    public ProviderPath Parent => _segments is { Length: > 1 } s ? new ProviderPath(s[..^1]) : Root;

    /// <summary>Parses a string path: splits it on <c>/</c> and <c>\</c>, dropping empty parts.</summary>
    /// <param name="path">The path, relative to the provider root. Null or empty is the root.</param>
    public static ProviderPath Parse(string? path) =>
        string.IsNullOrEmpty(path) ? Root : new ProviderPath(path.Split(_separators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A path with <paramref name="segments"/> added after this one's.</summary>
    /// <param name="segments">The segments to add.</param>
    public ProviderPath Append(params string[] segments) => new([.. Segments, .. segments]);

    /// <summary>Converts a string path; see <see cref="Parse"/>.</summary>
    /// <param name="path">The path.</param>
    public static implicit operator ProviderPath(string? path) => Parse(path);

    /// <summary>Converts an array of segments, as they are.</summary>
    /// <param name="segments">The segments.</param>
    public static implicit operator ProviderPath(string[]? segments) => new(segments);

    /// <summary>Creates a path from its segments; the same as the implicit conversion from an array.</summary>
    /// <param name="segments">The segments.</param>
    public static ProviderPath FromSegments(string[]? segments) => new(segments);

    /// <summary>Converts a string path; the same as <see cref="Parse"/>.</summary>
    /// <param name="path">The path.</param>
    public static ProviderPath FromString(string? path) => Parse(path);

    /// <inheritdoc />
    public bool Equals(ProviderPath other) => Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProviderPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in Segments)
            hash.Add(segment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>The segments joined with <c>/</c>.</summary>
    public override string ToString() => string.Join('/', Segments);

    /// <summary>Whether two paths have the same segments, compared ordinally.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    public static bool operator ==(ProviderPath left, ProviderPath right) => left.Equals(right);

    /// <summary>Whether two paths differ.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    public static bool operator !=(ProviderPath left, ProviderPath right) => !left.Equals(right);
}
