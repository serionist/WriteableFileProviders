using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>The gateway inside an app's middleware: HTTP logging, output caching and a fallback authorization policy.</summary>
[TestClass]
public sealed class PipelineTests
{
    [TestMethod]
    public async Task HttpLogging_DoesNotLogTheToken()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await GatewayHost.StartAsync(
            configureBuilder: builder =>
            {
                builder.Logging.AddProvider(logs);
                builder.Logging.SetMinimumLevel(LogLevel.Information);
                builder.Services.AddHttpLogging(options => options.LoggingFields = HttpLoggingFields.All);
            },
            configureApp: app => app.UseHttpLogging());
        await host.WriteAsync("doc.txt", "content");
        var url = await host.UrlAsync("doc.txt");
        var token = GatewayHost.TokenOf(url);

        using (var response = await host.Client.GetAsync(url))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await host.Client.GetAsync("/elsewhere/visible"))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

        var httpLogs = logs.Messages.Where(m => m.Category.StartsWith("Microsoft.AspNetCore.HttpLogging", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(httpLogs.Any(m => m.Text.Contains("/files/{token}/{fileName}", StringComparison.Ordinal)), string.Join("\n", httpLogs.Select(m => m.Text)));
        Assert.IsTrue(httpLogs.Any(m => m.Text.Contains("/elsewhere/visible", StringComparison.Ordinal)));
        Assert.IsFalse(httpLogs.Any(m => m.Text.Contains(token[^20..], StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OutputCaching_DoesNotCacheTheGateway()
    {
        await using var host = await GatewayHost.StartAsync(
            configureBuilder: builder => builder.Services.AddOutputCache(options => options.AddBasePolicy(policy => policy.Cache())),
            configureApp: app => app.UseOutputCache());
        await host.WriteAsync("doc.txt", "content");
        var url = await host.UrlAsync("doc.txt");

        using (var first = await host.Client.GetAsync(url))
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        await host.Provider().DeleteAsync("doc.txt");
        using var second = await host.Client.GetAsync(url);

        Assert.AreEqual(HttpStatusCode.NotFound, second.StatusCode);
    }

    [TestMethod]
    public async Task FallbackAuthorizationPolicy_DoesNotApplyToTheGateway()
    {
        await using var host = await GatewayHost.StartAsync(
            configureBuilder: builder =>
            {
                builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
                builder.Services.AddAuthorization(options =>
                    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            },
            configureApp: app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
            });
        await host.WriteAsync("doc.txt", "content");
        var url = await host.UrlAsync("doc.txt");

        using var gateway = await host.Client.GetAsync(url);
        using var other = await host.Client.GetAsync("/issue?provider=files&path=doc.txt&access=Read");

        Assert.AreEqual(HttpStatusCode.OK, gateway.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.OK, other.StatusCode);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, string Text)> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string, string)> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue((category, formatter(state, exception)));
        }
    }
}
