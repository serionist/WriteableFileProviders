namespace WriteableFileProviders.SharePoint
{
    public class SharePointFileProviderOptions : FileProviderOptions
    {
        public SharePointFileProviderOptions(string siteUrl, string documentLibraryName, string tenantId, string clientId, string clientSecret, string workFolderPath)
        {
            SiteUrl = siteUrl;
            DocumentLibraryName = documentLibraryName;
            TenantId = tenantId;
            ClientId = clientId;
            ClientSecret = clientSecret;
            WorkFolderPath = workFolderPath;
        }

        public SharePointFileProviderOptions() { }

        public string SiteUrl { get; set; } // The SharePoint site URL (e.g., https://tenant.sharepoint.com/sites/sitename)
        public string DocumentLibraryName { get; set; }
        public string TenantId { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        // Optional path, like: "MainFolder/WorkFolder/Test"
        public string WorkFolderPath { get; set; }
    }
}
