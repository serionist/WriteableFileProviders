using System.Runtime.CompilerServices;

namespace WriteableFileProviders;

public abstract partial class WriteableFileProvider<TOptions>
{
    private enum Operation
    {
        Info,
        List,
        Read,
        Write,
        Delete,
        Move,
        Copy,
        LocalCopy,
        Sign,
        Watch,
    }

    /// <summary>
    /// Whether an exception of the store means "not found": the public members turn it into a
    /// <see cref="FileNotFoundException"/> for the path, with the exception inside. The default is false.
    /// </summary>
    /// <param name="exception">The store's exception.</param>
    protected virtual bool IsNotFound(Exception exception) => false;

    /// <summary>
    /// Whether an exception of the store means the store refused the credential or the permission, such as an HTTP 401
    /// or 403 from its SDK: the public members turn it into an <see cref="UnauthorizedAccessException"/>, with the
    /// exception inside. The default is false; an <see cref="UnauthorizedAccessException"/> the provider throws itself
    /// passes through as it is.
    /// </summary>
    /// <param name="exception">The store's exception.</param>
    protected virtual bool IsAccessDenied(Exception exception) => false;

    /// <summary>
    /// Opens a stream for a read the provider adds as a public member of its own (such as a version of a file), under the
    /// same error policy as <see cref="ReadFileAsync"/>: an exception that <paramref name="read"/> throws, or that a read
    /// of the stream it returns throws, reaches the caller in the contract's types (see the class remarks).
    /// </summary>
    /// <param name="path">The file read, for the messages.</param>
    /// <param name="read">Opens the stream.</param>
    /// <param name="cancellationToken">The caller's token, which <paramref name="read"/> receives.</param>
    /// <returns>The stream, whose failures are mapped too.</returns>
    protected async Task<Stream> ReadWithContractAsync(ResolvedPath path, Func<CancellationToken, Task<Stream>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return Contract(await read(cancellationToken).ConfigureAwait(false), Operation.Read, path);
        }
        catch (Exception e) when (Map(e, Operation.Read, path, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    // The one place where an exception that escaped the store becomes one of the contract's types: the exception to throw
    // instead, or null when the exception is the contract's already and passes through as it is.
    private Exception? Map(Exception exception, Operation operation, ResolvedPath? path, ResolvedPath? destination, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            // The process, not the store, ran out.
            case OutOfMemoryException:
                return null;
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return null;
            case OperationCanceledException:
                return new IOException(
                    $"{Describe(operation, path, destination)} did not complete: the store or its client gave up, as on a timeout; the caller's token was not cancelled ({exception.GetType().Name}).",
                    exception);
        }
        if (path is not null && exception is not FileNotFoundException && Recognises(IsNotFound, exception))
            return NotFound(path, exception);
        if (exception is not UnauthorizedAccessException && Recognises(IsAccessDenied, exception))
        {
            return new UnauthorizedAccessException(
                $"{Describe(operation, path, destination)} was refused: the store did not accept the credential, or it lacks the permission ({exception.GetType().Name}).",
                exception);
        }
        if (exception is ArgumentException or IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
            return null;
        if (cancellationToken.IsCancellationRequested)
            return new OperationCanceledException($"{Describe(operation, path, destination)} was cancelled ({exception.GetType().Name}).", exception, cancellationToken);
        return new IOException(
            $"{Describe(operation, path, destination)} failed ({exception.GetType().Name}); the inner exception is what the store or its client threw.",
            exception);
    }

    // A provider's predicate runs inside an exception filter, where an exception of its own would be swallowed and the
    // store's exception would escape unmapped: one that fails counts as "no".
    private static bool Recognises(Func<Exception, bool> predicate, Exception exception)
    {
        try
        {
            return predicate(exception);
        }
#pragma warning disable CA1031 // A failing predicate must not let the store's exception escape unmapped.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static string Describe(Operation operation, ResolvedPath? path, ResolvedPath? destination)
    {
        var name = path is null ? "" : path.IsRoot ? "the root folder" : $"'{path}'";
        return operation switch
        {
            Operation.Info => $"Getting {name}",
            Operation.List => $"Listing {name}",
            Operation.Read => $"Reading {name}",
            Operation.Write => $"Writing {name}",
            Operation.Delete => $"Deleting {name}",
            Operation.Move => $"Moving {name} to '{destination}'",
            Operation.Copy => $"Copying {name} to '{destination}'",
            Operation.LocalCopy => $"Opening a local copy of {name}",
            Operation.Sign => $"Signing a URL for {name}",
            _ => "Watching for changes",
        };
    }

    // The stream a read member returns: the provider's, with its failures mapped.
    private ContractStream Contract(Stream stream, Operation operation, ResolvedPath path) =>
        new(stream, (exception, cancellationToken) => Map(exception, operation, path, null, cancellationToken));

    private async IAsyncEnumerable<IWriteableFileInfo> EnumerateResolvedAsync(ResolvedPath path, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<IWriteableFileInfo> entries;
        try
        {
            var source = recursive ? EnumerateRecursiveCoreAsync(path, cancellationToken) : EnumerateCoreAsync(path, cancellationToken);
            entries = source.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception e) when (Map(e, Operation.List, path, null, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await entries.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (Map(e, Operation.List, path, null, cancellationToken) is { } mapped)
                {
                    throw mapped;
                }
                if (!more)
                    break;
                yield return entries.Current;
            }
        }
        finally
        {
            try
            {
                await entries.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (Map(e, Operation.List, path, null, cancellationToken) is { } mapped)
            {
#pragma warning disable CA2219 // A failure to release the listing is reported as the listing's failure, in the contract's type.
                throw mapped;
#pragma warning restore CA2219
            }
        }
    }
}
