using System.Diagnostics.CodeAnalysis;

// The 2026 API named this parameter readOnly, and callers pass it by name; renaming it for Visual Basic would break them.
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The 2026 parameter name; callers pass it by name.",
    Scope = "member", Target = "~M:WriteableFileProviders.IWriteableFileProvider.CreateLocalFileStreamAsync(WriteableFileProviders.ProviderPath,System.Boolean,System.Threading.CancellationToken)~System.Threading.Tasks.Task{WriteableFileProviders.LocalFileStream}")]
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The 2026 parameter name; callers pass it by name.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProvider`1.CreateLocalFileStreamCoreAsync(WriteableFileProviders.ResolvedPath,System.Boolean,System.Threading.CancellationToken)~System.Threading.Tasks.Task{WriteableFileProviders.LocalFileStream}")]

// RS0026 and RS0027 guard adding optional parameters after a release. These overloads are the 2027 surface as designed:
// the sync forms and the IFileInfo forms mirror the interface's members, each with its own optional options and token.
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProviderExtensions.UpdateFileAsync(WriteableFileProviders.IWriteableFileProvider,WriteableFileProviders.ProviderPath,System.IO.Stream,WriteableFileProviders.UpdateFileMode,System.Threading.CancellationToken)~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProviderExtensions.UpdateFileAsync(WriteableFileProviders.IWriteableFileProvider,Microsoft.Extensions.FileProviders.IFileInfo,System.IO.Stream,WriteableFileProviders.UpdateFileOptions,System.Threading.CancellationToken)~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProviderExtensions.UpdateFile(WriteableFileProviders.IWriteableFileProvider,Microsoft.Extensions.FileProviders.IFileInfo,System.IO.Stream,WriteableFileProviders.UpdateFileOptions)")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProviderExtensions.GetDownloadUrlAsync(WriteableFileProviders.IWriteableFileProvider,WriteableFileProviders.ProviderPath,System.TimeSpan,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Uri}")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProviderExtensions.GetDownloadUrlAsync(WriteableFileProviders.IWriteableFileProvider,Microsoft.Extensions.FileProviders.IFileInfo,System.TimeSpan,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Uri}")]
[assembly: SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "The 2027 surface as designed, before any release.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProviderExtensions.UpdateFile(WriteableFileProviders.IWriteableFileProvider,WriteableFileProviders.ProviderPath,System.IO.Stream,WriteableFileProviders.UpdateFileOptions)")]

// Overrides of Stream's members, whose optional token Stream declares.
[assembly: SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "An override of Stream's member, as Stream declares it.",
    Scope = "member", Target = "~M:WriteableFileProviders.LocalFileStream.ReadAsync(System.Memory{System.Byte},System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{System.Int32}")]
[assembly: SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "An override of Stream's member, as Stream declares it.",
    Scope = "member", Target = "~M:WriteableFileProviders.LocalFileStream.WriteAsync(System.ReadOnlyMemory{System.Byte},System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask")]
[assembly: SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "An override of Stream's member, as Stream declares it.",
    Scope = "member", Target = "~M:WriteableFileProviders.RangeReadStream.ReadAsync(System.Memory{System.Byte},System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{System.Int32}")]
