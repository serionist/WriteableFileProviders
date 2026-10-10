namespace WriteableFileProviders;

/// <summary>How <see cref="IWriteableFileProvider.UpdateFileAsync"/> writes the content.</summary>
public enum UpdateFileMode
{
    /// <summary>Replace the file's content, or create the file.</summary>
    Overwrite,

    /// <summary>Add the content after the file's content, or create the file.</summary>
    Append,
}
