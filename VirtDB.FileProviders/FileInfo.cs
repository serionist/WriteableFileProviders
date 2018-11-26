using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders
{
    public class FileInfo:IFileInfo
    {
        public Stream CreateReadStream()
        {
            throw new NotImplementedException();
        }

        public bool Exists { get; }
        public long Length { get; }
        public string PhysicalPath { get; }
        public string Name { get; }
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory { get; }
    }
}
