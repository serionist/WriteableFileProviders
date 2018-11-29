using System;
using System.Collections.Generic;
using System.Text;

namespace WriteableFileProviders
{
    public class FileProviderOptions
    {
        public virtual string RootDirectoryPath
        {
            get => RootDirectorySegments == null ? "" : string.Join("/", RootDirectorySegments) + "/";
            set => RootDirectorySegments = value?.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        }
        public string[] RootDirectorySegments { get; set; }
    }
}
