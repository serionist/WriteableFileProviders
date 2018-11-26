using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders
{
    public class FileInfo:IFileInfo
    {
        public FileInfo(IWriteableFileProvider provider, bool exists, long length, string physicalPath, string name, DateTimeOffset lastModified, bool isDirectory)
        {
            Provider = provider;
            Exists = exists;
            Length = length;
            PhysicalPath = physicalPath;
            Name = name;
            LastModified = lastModified;
            IsDirectory = isDirectory;
        }

        public Stream CreateReadStream()
        {
            return Provider.ReadFile(PhysicalPath);
        }
        public IWriteableFileProvider Provider { get; }
        public bool Exists { get; }
        public long Length { get; }
        public string PhysicalPath { get; }
        public string Name { get; }
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory { get; }
    }
}
