using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WriteableFileProviders.Physical
{
    public class PhysicalFileProviderOptions:FileProviderOptions
    {
        public override string RootDirectoryPath
        {
            get => string.Join(Path.DirectorySeparatorChar.ToString(), base.RootDirectorySegments ?? new string[0]);
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    this.RootDirectorySegments = new string[0];
                    return;
                }
                else
                {
                    this.RootDirectorySegments = WriteablePhysicalFileProvider.getPathSegments(value);
                }
               
            }
        }
    }
}
