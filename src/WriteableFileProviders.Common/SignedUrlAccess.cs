namespace WriteableFileProviders;

/// <summary>What a signed URL allows.</summary>
public enum SignedUrlAccess
{
    /// <summary>Download the file (GET).</summary>
    Read,

    /// <summary>Upload the file only if it does not exist yet.</summary>
    Create,

    /// <summary>Upload the file, creating or replacing it.</summary>
    Write,
}
