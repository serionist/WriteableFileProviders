# Errors

Every member of every provider throws only these types. The store's or SDK's own exception is the `InnerException`,
for when you need its status or error code.

| Type | When |
|---|---|
| `ArgumentException` (and `ArgumentNullException`, `ArgumentOutOfRangeException`) | A path or an option the provider refuses. |
| `FileNotFoundException` | The file doesn't exist, or is a folder. |
| `FileExistsException` | The destination of a copy or a move exists. |
| `FileAlreadyExistsException` | A write with `IfNotExists` found a file at its path. It is a `PreconditionFailedException`. |
| `PreconditionFailedException` | `IfMatch` didn't hold, a pinned read's file changed, an append with no condition found its file changed five times in a row ([appends](reading-and-writing.md#append)), a copy's or a move's source changed while it was copied, or the store reported a conflict with a concurrent operation (the write can be retried); and, as its subtype above, `IfNotExists` didn't hold. |
| `NotSupportedException` | The provider or its store can't do it. |
| `InvalidOperationException` | The store isn't set up as configured (a missing container, bucket, library or root folder, or no credentials to find), or you tried to modify the root. |
| `UnauthorizedAccessException` | The store refused the credential or the permission: HTTP 401 or 403, or a refused sign-in. |
| `IOException` | Any other failure of the store, the service or the network, timeouts included. |
| `ObjectDisposedException` | The provider is disposed. |
| `OperationCanceledException` | Only when your cancellation token cancelled the call. |

- `FileExistsException` and `PreconditionFailedException` derive from `IOException`, and `FileAlreadyExistsException`
  from `PreconditionFailedException`, so a `catch (PreconditionFailedException)` catches it too. Catch it first to
  tell "someone created it" from "someone changed it".
- `FileExistsException` (a copy's or a move's destination) and `FileAlreadyExistsException` (an `IfNotExists` write)
  are different types: neither catches the other.
- A write with `IfNotExists` where a folder is at the path gets what a write without conditions gets there:
  `IOException` where folders are real (Physical, Box, SharePoint), the file stored beside the folder where folders are
  name prefixes (the object stores).
- An exception that already is one of these types passes as it is. `OutOfMemoryException` passes unmapped.
- The same holds for reads of a stream a member returned, each step of `EnumerateAsync`, and a local stream's commit.
- If the stream you give `UpdateFileAsync` fails, its exception reaches you as it is when it is one of these types (an
  `IOException` usually is), and inside an `IOException` otherwise.

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

What each store's answers become.

### Physical

- `UnauthorizedAccessException` and `IOException` from the file system (a sharing violation, a full disk) pass through
  as .NET raises them.
- Wherever the operating system refuses the process access to a file or folder, any member throws
  `UnauthorizedAccessException`, a read or a listing as well as a write. A path the system won't look up at all, such
  as one inside a folder the process may not enter, reads as missing.
- A missing file is `FileNotFoundException`, naming the path under the root. Anything else is `IOException`.

### Azure Blob Storage

- `UnauthorizedAccessException`: 401, 403, or a sign-in Microsoft Entra ID refuses (`AuthenticationFailedException`).
  A move whose source delete is refused removes its copy first.
- `InvalidOperationException`: a missing container, or no credential to use (`CredentialUnavailableException`).
- `IOException`: any other failure of Azure or the network, after the SDK's retries, timeouts included. The inner
  exception is the SDK's `RequestFailedException`, or what it threw.

### Amazon S3

- `UnauthorizedAccessException`: any 401 or 403, whatever the code (`AccessDenied`, `InvalidAccessKeyId`,
  `SignatureDoesNotMatch`, `InvalidPayer`, or none, as a `HEAD` answers), and an expired or invalid session token
  (`ExpiredToken`, `InvalidToken`, a 400).
- `InvalidOperationException`: a bucket name another account owns, with `CreateBucketIfMissing`, or no credentials for
  `UseDefaultCredentials` to find.
- `IOException`: any other failure of S3 or the network, after the SDK's retries. That includes timeouts, an archived
  object (`InvalidObjectState`, `ObjectNotInActiveTierError`) and a skewed clock (`RequestTimeTooSkewed`), though they
  answer 403. The inner exception is the SDK's `AmazonS3Exception`, or what it threw.

### MinIO

- `UnauthorizedAccessException`: any 401 or 403, whatever the code (`AccessDenied`, `InvalidAccessKeyId`,
  `SignatureDoesNotMatch`, a code of the server's own, or none), and an expired or invalid session token
  (`ExpiredToken`, `InvalidToken`, a 400).
- `InvalidOperationException`: a bucket name another account owns, with `CreateBucketIfMissing`.
- `IOException`: any other failure of the server or the network, after the SDK's retries. That includes timeouts, a
  wrong `Region`, an archived object and a skewed clock. The inner exception is the SDK's `AmazonS3Exception`, or what
  it threw.

### Google Cloud Storage

- `UnauthorizedAccessException`: 401, 403, or Google's token endpoint refusing the credential
  (`TokenResponseException`). A credential without `storage.objects.list` gets it from every member that lists
  ([README](../../src/WriteableFileProviders.GoogleCloudStorage/README.md)).
- `InvalidOperationException`: no Application Default Credentials to find, from the first call.
- `IOException`: any other failure of Cloud Storage or the network, timeouts included. The inner exception is the
  SDK's `GoogleApiException`, or what it threw.

### Box

- `UnauthorizedAccessException`: 401, 403, or a sign-in Box refuses (a 400 from its token endpoint).
- `InvalidOperationException`: a `RootFolderId` that doesn't exist or the app can't see.
- `ArgumentException`: a name Box refuses.
- `IOException`: any other failure of Box or the network. The inner exception is the SDK's `BoxApiException` (status in
  `ResponseInfo.StatusCode`) or the network's.
- A download from Box's pre-authenticated URL fails with `FileNotFoundException` or `IOException` carrying only its
  HTTP status: the URL is a credential, so no error holds it.

A `BoxApiException` keeps the request it sent: the access token, extra headers of the client (such as a shared-link
password), the query string and the body (a token request's holds the client secret).

- When the provider built its client, or was given exactly a `BoxClient`, it strips that request. Each wrapped
  `BoxApiException` keeps Box's status, code and message, the method, the URL without the query, and the headers and
  parameters that diagnose it (`As-User`, `If-Match`, `Range`, the digests, the content type, listing and version).
- Any other `IBoxClient`, a class derived from `BoxClient` included, is used as it is. Its errors are its own, and may
  carry its credentials.

### SharePoint

- `UnauthorizedAccessException`: 401, 403, or a sign-in Microsoft Entra ID refuses (`AuthenticationFailedException`,
  inside).
- `InvalidOperationException`: a missing site or library, naming `SiteUrl` or `DocumentLibraryName`, or no credential
  to use with `UseDefaultAzureCredential` (`CredentialUnavailableException`).
- `IOException`: other failures, timeouts included. The SDK retries 429, 503 and 504 three times, honouring
  `Retry-After`, first. The message names Graph's error.
- Only a 404 means not found.
