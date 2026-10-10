using System;
using System.Threading.Tasks;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.AzureBlob;

namespace WriteableFileProviders.Tests.AzureBlob
{
    /// <summary>
    /// Connection details of the Azurite blob emulator that <see cref="Emulators"/> starts through Testcontainers.
    /// Tests are inconclusive when Docker is not available.
    /// </summary>
    internal static class Azurite
    {
        public const string AccountName = "devstoreaccount1";
        // Azurite's well-known development account key, published by Microsoft; not a secret.
        public const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

        /// <summary>host:port/devstoreaccount1 of the running emulator.</summary>
        public static string Url => new Uri(Emulators.Azurite.GetBlobEndpoint()).Authority + "/" + AccountName;

        public static string NewContainerName() => $"wfp-test-{Guid.NewGuid():N}";

        public static void RequireRunning() => _ = Emulators.Azurite;

        public static WriteableAzureBlobFileProvider CreateProvider(string containerName, string rootDirectoryPath = null)
        {
            RequireRunning();
            return new WriteableAzureBlobFileProvider(Options(containerName, rootDirectoryPath));
        }

        public static AzureFileProviderOptions Options(string containerName, string rootDirectoryPath = null)
        {
            return new AzureFileProviderOptions
            {
                AccountName = AccountName,
                AccountKey = AccountKey,
                ContainerName = containerName,
                Protocol = "http",
                Url = Url,
                RootDirectoryPath = rootDirectoryPath
            };
        }

        public static BlobContainerClient Container(string containerName, BlobClientOptions options = null)
        {
            return new BlobContainerClient(new Uri($"http://{Url}/{containerName}"),
                new StorageSharedKeyCredential(AccountName, AccountKey), options);
        }

        public static async Task DeleteContainer(string containerName)
        {
            await Container(containerName).DeleteIfExistsAsync();
        }
    }
}
