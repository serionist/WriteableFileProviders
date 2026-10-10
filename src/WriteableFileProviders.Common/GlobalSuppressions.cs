using System.Diagnostics.CodeAnalysis;

// The 2026 API named this parameter readOnly, and callers pass it by name; renaming it for Visual Basic would break them.
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The 2026 parameter name; callers pass it by name.",
    Scope = "member", Target = "~M:WriteableFileProviders.IWriteableFileProvider.CreateLocalFileStreamAsync(WriteableFileProviders.ProviderPath,System.Boolean,System.Threading.CancellationToken)~System.Threading.Tasks.Task{WriteableFileProviders.LocalFileStream}")]
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The 2026 parameter name; callers pass it by name.",
    Scope = "member", Target = "~M:WriteableFileProviders.WriteableFileProvider`1.CreateLocalFileStreamCoreAsync(WriteableFileProviders.ResolvedPath,System.Boolean,System.Threading.CancellationToken)~System.Threading.Tasks.Task{WriteableFileProviders.LocalFileStream}")]
