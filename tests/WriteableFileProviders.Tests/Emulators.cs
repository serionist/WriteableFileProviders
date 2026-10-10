using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Tests
{
    /// <summary>
    /// The emulators the tests run against, started once per test run through Testcontainers and removed when the run
    /// ends. Without Docker the tests that need one are inconclusive, unless WFP_REQUIRE_DOCKER=1 (set in CI), which
    /// makes them fail.
    /// </summary>
    internal static class Emulators
    {
        public const string S3MockImage = "adobe/s3mock:5.2.3";
        public const string S3MockBucket = "wfp-test";

        private static readonly Lazy<Task<IContainer>> s3Mock = new Lazy<Task<IContainer>>(async () =>
        {
            var container = new ContainerBuilder(S3MockImage)
                .WithEnvironment("COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS", S3MockBucket)
                .WithPortBinding(9090, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
                .Build();
            await container.StartAsync().ConfigureAwait(false);
            return container;
        });

        public static bool DockerRequired => Environment.GetEnvironmentVariable("WFP_REQUIRE_DOCKER") == "1";

        /// <summary>The S3Mock container's host:port, with the bucket <see cref="S3MockBucket"/>.</summary>
        public static string S3MockHostAndPort
        {
            get
            {
                var container = start(s3Mock, "S3Mock");
                return $"{container.Hostname}:{container.GetMappedPublicPort(9090)}";
            }
        }

        private static T start<T>(Lazy<Task<T>> container, string name)
        {
            try
            {
                return container.Value.GetAwaiter().GetResult();
            }
            catch (Exception e) when (!DockerRequired)
            {
                Assert.Inconclusive($"{name} could not start through Testcontainers (is Docker running?): {e.Message}");
                throw;
            }
        }
    }
}
