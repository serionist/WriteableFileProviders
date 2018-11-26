using System;
using System.Collections.Generic;
using System.Text;

namespace WriteableFileProviders.AzureBlob
{
    public class AzureFileProviderOptions
    {
        public string ConnectionString { get; set; }
        public string ContainerName { get; set; }
    }
}
