# Errors

Every member of every provider throws only the types below, so code that handles a failure catches the same
exceptions whichever store it runs on. The store's or the SDK's own exception travels as the `InnerException`, for
when you need its status or error code.

## What a member throws

| Type | When |
|---|---|
| `ArgumentException` (`ArgumentNullException`, `ArgumentOutOfRangeException`) | A path or an option the provider refuses. |
| `FileNotFoundException` | A file the member needs does not exist, or is a folder. |
| `FileExistsException` | The destination of a copy or a move exists. |
| `PreconditionFailedException` | `IfMatch` or `IfNotExists` did not hold, or a pinned read's file changed. |
| `NotSupportedException` | A feature this provider or its store cannot give. |
| `InvalidOperationException` | The store is not set up as configured: a missing container, bucket, library or root folder, or no credentials to find; or the root was to be modified. |
| `UnauthorizedAccessException` | The store refused the credential or the permission: an HTTP 401 or 403, or a sign-in its identity service refused. |
| `IOException` | Any other failure of the store, the service or the network, timeouts included. |
| `ObjectDisposedException` | The provider is disposed. |
| `OperationCanceledException` | Only when your cancellation token cancelled the call. |

`FileExistsException` and `PreconditionFailedException` derive from `IOException`, so a handler of `IOException` sees
them too. An exception that already derives from one of these types (an `IOException` of the network, say) passes as
it is; `OutOfMemoryException` passes unmapped.

The same holds for the reads of a stream a member returned, for each step of `EnumerateAsync`, and for a local stream's
commit. A failure of the stream you give `UpdateFileAsync` reaches you as it is when it is one of these types, as an
`IOException` usually is, and inside an `IOException` otherwise.

```csharp
try
{
    await files.UpdateFileAsync(path, stream, new UpdateFileOptions { IfMatch = etag }, ct);
}
catch (PreconditionFailedException)
{
    // someone else wrote the file first: read it again and retry, or report the conflict
}
catch (UnauthorizedAccessException)
{
    // the credential or its permissions are wrong: not worth a retry
}
catch (IOException e)
{
    // the store or the network failed, after the SDK's own retries: e.InnerException has the details
}
```

## Provider by provider

Which of its store's answers each provider maps to which type. Moving from 2026, where each SDK's exceptions escaped
as they were, is in the [migration guide](../migration-2027.md#what-a-member-throws).

### Physical

The file system's own: `UnauthorizedAccessException` and `IOException` (a sharing violation, a full disk, a missing
folder) pass through as .NET raises them, with no SDK in between; a missing file is `FileNotFoundException` naming the
path under the root, with the system's exception inside; anything else that escapes is `IOException`, with it inside.
Where the operating system refuses the process access to a file or a folder that a member opens, lists, writes, moves
or deletes, the member throws `UnauthorizedAccessException`, a read or a listing as well as a write; a path the system
will not look up at all, such as one inside a folder the process may not enter, reads as missing.
The root folder is an operating system path, which the system resolves, dot segments included, rather than a path the
provider checks.

### Azure Blob Storage

Azure's 401 and 403, and a sign-in Microsoft Entra ID refuses (Azure.Identity's `AuthenticationFailedException`), throw
`UnauthorizedAccessException`, a move whose source delete is refused included (its copy is removed first); a sign-in
that finds no credential to use (`CredentialUnavailableException`), like a missing container,
`InvalidOperationException`; any other failure of Azure or the network, after the SDK's retries, `IOException`,
timeouts included. The SDK's `RequestFailedException`, or what it threw, is the `InnerException`.

### Amazon S3

S3's 401 and 403, whatever the code (`AccessDenied`, `InvalidAccessKeyId`, `SignatureDoesNotMatch`, `InvalidPayer` and
the like, or none, as a HEAD answers), and a session token that expired or is not valid (`ExpiredToken`,
`InvalidToken`, a 400), throw `UnauthorizedAccessException`, for a file delete as for a folder's, except an archived
object (`InvalidObjectState`, `ObjectNotInActiveTierError`) and a skewed clock (`RequestTimeTooSkewed`); a bucket name
another account owns, with `CreateBucketIfMissing`, or no credentials for `UseDefaultCredentials` to find,
`InvalidOperationException`; any other failure of S3 or the network, after the SDK's retries, `IOException`: timeouts,
the archived object and the skewed clock included. The SDK's `AmazonS3Exception`, or what it threw, is the
`InnerException`.

### MinIO

The server's 401 and 403, whatever the code (`AccessDenied`, `InvalidAccessKeyId`, `SignatureDoesNotMatch`, a code of
the server's own, or none, as a HEAD answers), and a session token that expired or is not valid (`ExpiredToken`,
`InvalidToken`, a 400), throw `UnauthorizedAccessException`, except an archived object (`InvalidObjectState`,
`ObjectNotInActiveTierError`) and a skewed clock (`RequestTimeTooSkewed`); a bucket name another account owns, with
`CreateBucketIfMissing`, `InvalidOperationException`; any other failure of the server or the network, after the SDK's
retries, `IOException`: timeouts, a wrong `Region`, the archived object and the skewed clock included. The SDK's
`AmazonS3Exception`, or what it threw, is the `InnerException`.

### Google Cloud Storage

Cloud Storage's 401 and 403, and Google's token endpoint refusing the credential (`TokenResponseException`), throw
`UnauthorizedAccessException`; no Application Default Credentials to find, `InvalidOperationException`, from the first
call; any other
failure of Cloud Storage or the network, `IOException`, timeouts included; the SDK's `GoogleApiException`, or what it
threw, is the `InnerException`. A credential without `storage.objects.list` gets `UnauthorizedAccessException` from
every member that has to list (its [README](../../src/WriteableFileProviders.GoogleCloudStorage/README.md#authentication)
names them).

### Box

Box's 401 and 403, and a sign-in Box refuses (a 400 from its token endpoint), throw `UnauthorizedAccessException`; a
`RootFolderId` that does not exist or the app cannot see, `InvalidOperationException`; a name Box refuses,
`ArgumentException`; any other failure of Box or the network, `IOException`; with the SDK's `BoxApiException` (its
status in `ResponseInfo.StatusCode`) or network exception as the `InnerException`. The download itself, from Box's
pre-authenticated URL, throws `FileNotFoundException` or `IOException` with its HTTP status only: that URL is a
credential, so no error carries it.

That `BoxApiException` keeps the request it sent: the access token in its `Authorization` header, any extra header the
client was built with (such as a `BoxApi` shared-link password), the query string, and the body (a token request's
holds the client secret). When the provider built its client, or was given exactly a `BoxClient`, every
`BoxApiException` it wraps keeps Box's status, code and message, and of the request only its method, its URL without
the query, and the headers and parameters that diagnose it (`As-User`, `If-Match`, `Range`, the digests, the content
type; the listing and version parameters). A provider given any other `IBoxClient`, a class derived from `BoxClient`
included, uses that client as it is: its errors are the client's own and may carry its credentials.

### SharePoint

Only a 404 means "not found". The SDK retries 429, 503 and 504 three times, honouring `Retry-After`; then the call
throws `IOException`. 401 and 403 throw `UnauthorizedAccessException`, and so does a sign-in Microsoft Entra ID refuses
(Azure.Identity's `AuthenticationFailedException`, inside); other failures `IOException`, timeouts included; the
message names Graph's error, and the SDK's or the network's exception is inside where there is one. A missing site or
library throws `InvalidOperationException` naming `SiteUrl` or `DocumentLibraryName` from every member that reaches the
library, and so does a sign-in that finds no credential to use (`CredentialUnavailableException`, with
`UseDefaultAzureCredential`).
