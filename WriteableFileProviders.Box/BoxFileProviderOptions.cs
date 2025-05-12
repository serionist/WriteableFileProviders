namespace WriteableFileProviders.Box
{
    public class BoxFileProviderOptions : FileProviderOptions
    {
        public BoxFileProviderOptions(string clientId, string clientSecret, string enterpriseId, string publicKeyId, 
            string privateKey, string passphrase, string rootFolderId)
        {
            ClientId = clientId;
            ClientSecret = clientSecret;
            EnterpriseId = enterpriseId;
            PublicKeyId = publicKeyId;
            PrivateKey = privateKey;
            Passphrase = passphrase;
            RootFolderId = rootFolderId;
        }

        public BoxFileProviderOptions() { }

        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string EnterpriseId { get; set; }
        public string PublicKeyId { get; set; }
        public string PrivateKey { get; set; }
        public string Passphrase { get; set; }
        public string RootFolderId { get; set; } = "0"; // Default Box root
    }
}
