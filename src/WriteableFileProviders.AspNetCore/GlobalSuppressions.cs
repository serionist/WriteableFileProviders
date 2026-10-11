using System.Diagnostics.CodeAnalysis;

// RS0026 guards adding optional parameters after a release. The URL service's two forms, for the app's provider and for
// a keyed one, are the 2027 surface as designed, both with the optional token every async member has: no call binds to
// both, since only the keyed form takes a string after the options.
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.AspNetCore.IWriteableFileUrlService.GetSignedUrlAsync(WriteableFileProviders.ProviderPath,WriteableFileProviders.SignedUrlOptions,System.Threading.CancellationToken)~System.Threading.Tasks.Task{WriteableFileProviders.SignedUrl}")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.AspNetCore.IWriteableFileUrlService.GetSignedUrlAsync(WriteableFileProviders.ProviderPath,WriteableFileProviders.SignedUrlOptions,System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task{WriteableFileProviders.SignedUrl}")]
