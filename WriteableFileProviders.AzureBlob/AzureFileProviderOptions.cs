using System;
using System.Collections.Generic;
using System.Text;

namespace WriteableFileProviders.AzureBlob
{
    public class AzureFileProviderOptions:FileProviderOptions
    {
        public string AccountName { get; set; }
        public string AccountKey { get; set; }
        public string ContainerName { get; set; }
      
    }
}
