using System.ComponentModel;

namespace WriteableFileProviders.AzureBlob;

/// <summary>
/// The 2026 name of <see cref="AzureBlobFileProviderOptions"/>, kept so that 2026 code such as
/// <c>new WriteableAzureBlobFileProvider(new AzureFileProviderOptions { ... })</c> compiles. It adds nothing: a provider
/// built from it is the same, and writes the same configuration. Like the 2026 class, it is not sealed. It goes in
/// 2028.0.0.
/// </summary>
[Obsolete("Use AzureBlobFileProviderOptions, the same options under the package's name. It goes in 2028.0.0.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public class AzureFileProviderOptions : AzureBlobFileProviderOptions
{
}
