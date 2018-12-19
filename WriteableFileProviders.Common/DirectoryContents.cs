using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders
{
    public class DirectoryContents: IDirectoryContents
    {

        public DirectoryContents(bool exists, IFileInfo[] infos)
        {
            Exists = exists;
            _files = infos;
        }

        private IFileInfo[] _files { get; }
        public bool Exists { get; }
        public IEnumerator<IFileInfo> GetEnumerator()
        {
            return _files.AsEnumerable().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _files.GetEnumerator();
        }
    }
}
