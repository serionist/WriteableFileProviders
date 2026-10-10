namespace WriteableFileProviders.AzureBlob
{
    public class AzureFileProviderOptions : FileProviderOptions
    {
        public string AccountName { get; set; }
        public string AccountKey { get; set; }
        public string ContainerName { get; set; }
        /// <summary>The endpoint scheme: <c>http</c> or <c>https</c>. Defaults to <c>https</c> when empty.</summary>
        public string Protocol { get; set; }
        public string Url { get; set; }

    }
}
