namespace WriteableFileProviders;

public abstract partial class WriteableFileProvider<TOptions>
{
    private const string RootModificationMessage = "The provider root cannot be deleted or modified.";
    private const string RootReadMessage = "The path must contain at least one segment.";
    private const int MaxDecodeRounds = 4;
    private static readonly char[] _segmentSeparators = ['/', '\\'];

    /// <summary>
    /// Validates a caller's path and places it under the provider root. Every part must pass
    /// <see cref="ValidateSegments"/> and <see cref="IsSegmentAllowed"/>, also after percent-decoding.
    /// </summary>
    /// <param name="path">The caller's path.</param>
    /// <exception cref="ArgumentException">A segment is not allowed.</exception>
    /// <exception cref="ObjectDisposedException">The provider is disposed.</exception>
    protected ResolvedPath Resolve(ProviderPath path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var parts = ValidateSegments(path.Segments);
        foreach (var part in parts)
        {
            if (!IsSegmentAllowed(part) || !DecodedForms(part)!.TrueForAll(IsSegmentAllowed))
                throw InvalidSegment(part);
        }
        return new ResolvedPath(Options.RootDirectorySegments, parts);
    }

    /// <summary>
    /// Splits the caller's segments on <c>/</c> and <c>\</c> and validates every part. A part is rejected when it is empty
    /// or whitespace, consists only of dots and spaces, contains a control character, or fails the same checks after any
    /// round of percent-decoding.
    /// </summary>
    /// <param name="segments">The caller's path segments (without the root segments).</param>
    /// <returns>The validated parts.</returns>
    /// <exception cref="ArgumentException">A part is not allowed.</exception>
    protected static string[] ValidateSegments(IEnumerable<string>? segments)
    {
        var ret = new List<string>();
        if (segments is null)
            return [];
        foreach (var segment in segments)
        {
            foreach (var part in (segment ?? "").Split(_segmentSeparators))
            {
                if (!IsValidPart(part))
                    throw InvalidSegment(part);
                var decoded = DecodedForms(part);
                if (decoded is null || !decoded.TrueForAll(d => Array.TrueForAll(d.Split(_segmentSeparators), IsValidPart)))
                    throw InvalidSegment(part);
                ret.Add(part);
            }
        }
        return [.. ret];
    }

    /// <summary>A provider's own segment rule, applied to every validated part and to each of its percent-decoded forms.</summary>
    /// <param name="part">A single path segment.</param>
    /// <returns>False to reject the segment.</returns>
    protected virtual bool IsSegmentAllowed(string part) => true;

    /// <summary>
    /// Whether a name the store lists can be named again by a path: it is one segment (no <c>/</c> or <c>\</c>) that
    /// passes the same checks as a caller's path, <see cref="ValidateSegments"/> and <see cref="IsSegmentAllowed"/>, also
    /// after percent-decoding. A listing leaves out every name for which this is false, and never walks into such a folder,
    /// so everything it lists can be read back.
    /// </summary>
    /// <param name="name">A file or folder name, as the store lists it.</param>
    /// <returns>True when a path can name it.</returns>
    protected bool IsAddressable(string name) =>
        !string.IsNullOrEmpty(name)
        && name.IndexOfAny(_segmentSeparators) < 0
        && IsValidPart(name)
        && DecodedForms(name) is { } decoded
        && decoded.TrueForAll(d => Array.TrueForAll(d.Split(_segmentSeparators), IsValidPart))
        && IsSegmentAllowed(name)
        && decoded.TrueForAll(IsSegmentAllowed);

    private ResolvedPath ResolveReadable(ProviderPath path)
    {
        var resolved = Resolve(path);
        return resolved.IsRoot ? throw new ArgumentException(RootReadMessage, nameof(path)) : resolved;
    }

    private ResolvedPath ResolveModifiable(ProviderPath path)
    {
        var resolved = Resolve(path);
        return resolved.IsRoot ? throw new InvalidOperationException(RootModificationMessage) : resolved;
    }

    // The percent-decoded forms of the part, one per round, or null when it still changes after MaxDecodeRounds.
    private static List<string>? DecodedForms(string part)
    {
        var ret = new List<string>();
        var current = part;
        for (var i = 0; i < MaxDecodeRounds; i++)
        {
            var decoded = Uri.UnescapeDataString(current);
            if (decoded == current)
                return ret;
            ret.Add(decoded);
            current = decoded;
        }
        return Uri.UnescapeDataString(current) == current ? ret : null;
    }

    private static bool IsValidPart(string part) =>
        !string.IsNullOrWhiteSpace(part) && part.Trim(' ', '.').Length != 0 && !part.Any(char.IsControl);

    private static ArgumentException InvalidSegment(string? part, string parameterName = "path")
    {
        var name = new string([.. (part ?? "").Select(c => char.IsControl(c) ? '?' : c)]);
        return new ArgumentException($"The path segment '{name}' is not allowed.", parameterName);
    }
}
