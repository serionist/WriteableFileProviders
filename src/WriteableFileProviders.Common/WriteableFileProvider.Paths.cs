namespace WriteableFileProviders;

public abstract partial class WriteableFileProvider<TOptions>
{
    private const string RootModificationMessage = "The provider root cannot be deleted or modified.";
    private const string RootReadMessage = "The path must contain at least one segment.";
    private const int MaxDecodeRounds = 4;
    private static readonly char[] _segmentSeparators = ['/', '\\'];

    /// <summary>
    /// Validates a caller's path and places it under the provider root: the root the options named when the provider was
    /// built, which a later change to the options does not move. Every part must pass <see cref="ValidateSegments"/> and
    /// <see cref="IsSegmentAllowed"/>, also after percent-decoding. <c>Resolve(ProviderPath.Root).AbsoluteSegments</c> is
    /// the root itself.
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
            if (!IsAllowedByProvider(part))
                throw InvalidSegment(part);
        }
        return new ResolvedPath(_rootSegments, parts);
    }

    /// <summary>
    /// Whether the base class checks <see cref="FileProviderOptions.RootDirectorySegments"/> when the provider is built,
    /// with the same rules as a caller's path (<see cref="ValidateSegments"/> and <see cref="IsSegmentAllowed"/>, also
    /// after percent-decoding), so that a root such as <c>tenants/../other</c> throws <see cref="ArgumentException"/>
    /// instead of naming a place outside itself. True by default. A provider whose root is not a path in its store, such
    /// as a folder of the local disk, returns false and checks its root itself. The base constructor reads it before the
    /// provider's own constructor runs, so it must not depend on the provider's state.
    /// </summary>
    protected virtual bool ValidatesRootDirectory => true;

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
        var parts = PartsOf(segments);
        return parts.Find(part => !IsPathPart(part)) is { } refused ? throw InvalidSegment(refused) : [.. parts];
    }

    /// <summary>
    /// A provider's own segment rule, applied to every validated part and to each of its percent-decoded forms: of a
    /// caller's path, of a name a listing returns (<see cref="IsAddressable"/>), and of the root (see
    /// <see cref="ValidatesRootDirectory"/>), which the base constructor checks before the provider's own constructor runs,
    /// so the rule must not depend on the provider's state.
    /// </summary>
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
        && IsPathPart(name)
        && IsAllowedByProvider(name);

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

    // The options' root, checked once when the provider is built (ValidatesRootDirectory) as a caller's path is checked:
    // the message of the ArgumentException to throw, or null when the root may be used.
    private string? RootRefusal(string[] root)
    {
        if (!ValidatesRootDirectory || PartsOf(root).Find(part => !IsPathPart(part) || !IsAllowedByProvider(part)) is not { } refused)
            return null;
        return $"RootDirectoryPath '{Printable(string.Join('/', root))}' cannot be the provider's root: its segment '{Printable(refused)}' is not "
            + "allowed in a path (no segment may be empty, only dots and spaces, hold a control character, or become one of those when "
            + "percent-decoded), and a root must not name a place outside itself.";
    }

    // The segments split on both separators, in order.
    private static List<string> PartsOf(IEnumerable<string>? segments) =>
        segments is null ? [] : [.. segments.SelectMany(segment => (segment ?? "").Split(_segmentSeparators))];

    // A part the common path rules accept, as it is and after every round of percent-decoding.
    private static bool IsPathPart(string part) =>
        IsValidPart(part)
        && DecodedForms(part) is { } decoded
        && decoded.TrueForAll(d => Array.TrueForAll(d.Split(_segmentSeparators), IsValidPart));

    // A part the provider's own rule accepts, as it is and in each of its percent-decoded forms; for a part IsPathPart took.
    private bool IsAllowedByProvider(string part) => IsSegmentAllowed(part) && DecodedForms(part)!.TrueForAll(IsSegmentAllowed);

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

    private static ArgumentException InvalidSegment(string? part, string parameterName = "path") =>
        new($"The path segment '{Printable(part)}' is not allowed.", parameterName);

    private static string Printable(string? text) => new([.. (text ?? "").Select(c => char.IsControl(c) ? '?' : c)]);
}
