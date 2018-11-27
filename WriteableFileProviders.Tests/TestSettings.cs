using System;
using System.Collections.Generic;
using System.Text;

namespace WriteableFileProviders.Tests
{
    public class TestSettings
    {
        public string PhysicalFileProvider_RootPath { get; set; }
        public string AzureBlobFileProvider_ConnectionString { get; set; }
        public string AzureBlobFileProvider_ContainerName { get; set; }
    }
}
