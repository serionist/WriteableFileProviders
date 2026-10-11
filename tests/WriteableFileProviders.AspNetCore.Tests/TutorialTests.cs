using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Physical;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>
/// The root README's tutorial, download URLs for local files: the app below is the README's, line for line, apart from
/// the lines marked "test", which run it on TestServer over a temp folder instead of <c>files</c>.
/// </summary>
[TestClass]
public sealed class TutorialTests
{
    [TestMethod]
    public async Task LocalFiles_GetDownloadUrls_ThatTheGatewayServes()
    {
        var files = Path.Combine(Path.GetTempPath(), "wfp-tutorial-" + Guid.NewGuid().ToString("N"));
        string[] args = [];
        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseTestServer();                                                  // test
            builder.Logging.ClearProviders();                                                 // test
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();        // test
            builder.Services.AddSingleton<IWriteableFileProvider>(new WriteablePhysicalFileProvider(files));
            builder.Services.AddWriteableFileProviderGateway(new Uri("http://localhost:5000"));
            var app = builder.Build();
            app.MapWriteableFileProviderGateway();
            app.MapGet("/link/{**path}", async (string path, IWriteableFileUrlService urls) =>
                (await urls.GetSignedUrlAsync(path, new SignedUrlOptions { Validity = TimeSpan.FromHours(1) })).Url.ToString());
            await app.StartAsync();                                                           // test: app.Run();

            await using (app)
            {
                var provider = app.Services.GetRequiredService<IWriteableFileProvider>();
                await provider.UpdateFileAsync("reports/2026.csv", TestStreams.Text("year,total\n2026,42\n"));
                using var client = app.GetTestClient();

                var link = new Uri(await client.GetStringAsync(new Uri("/link/reports/2026.csv", UriKind.Relative)));
                using var download = await client.GetAsync(link);

                Assert.AreEqual("http://localhost:5000/files/", link.GetLeftPart(UriPartial.Path)[..28]);
                Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
                Assert.AreEqual("year,total\n2026,42\n", await download.Content.ReadAsStringAsync());
                Assert.AreEqual("2026.csv", download.Content.Headers.ContentDisposition?.FileNameStar);
                await app.StopAsync();
                provider.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(files))
                Directory.Delete(files, recursive: true);
        }
    }
}
