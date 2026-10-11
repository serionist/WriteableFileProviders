using System.Diagnostics.CodeAnalysis;

// RS0026 guards adding optional parameters after a release. These four constructors are 2026's, kept as they were so
// existing callers compile; each has always ended with an optional storage class.
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2026 constructors, kept as they were.",
    Scope = "member", Target = "~M:WriteableFileProviders.AmazonS3.AmazonS3FileProviderOptions.#ctor(System.String,System.String,System.String,System.String,System.String,Amazon.S3.S3StorageClass)")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2026 constructors, kept as they were.",
    Scope = "member", Target = "~M:WriteableFileProviders.AmazonS3.AmazonS3FileProviderOptions.#ctor(System.String,System.String,System.String,System.String,Amazon.S3.S3Region,Amazon.S3.S3StorageClass)")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2026 constructors, kept as they were.",
    Scope = "member", Target = "~M:WriteableFileProviders.AmazonS3.AmazonS3FileProviderOptions.#ctor(System.String,System.String,System.String,Amazon.S3.S3StorageClass)")]
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The 2026 constructors, kept as they were.",
    Scope = "member", Target = "~M:WriteableFileProviders.AmazonS3.AmazonS3FileProviderOptions.#ctor(System.String,System.String,Amazon.S3.S3Region,Amazon.S3.S3StorageClass)")]
