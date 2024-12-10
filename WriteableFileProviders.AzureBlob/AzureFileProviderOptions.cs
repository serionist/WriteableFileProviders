namespace WriteableFileProviders.AzureBlob
{
    public class AzureFileProviderOptions : FileProviderOptions
    {
        public string AccountName { get; set; }
        public string AccountKey { get; set; }
        public string ContainerName { get; set; }
        public string Protocol { get; set; }
        public string Url { get; set; }

    }
}
