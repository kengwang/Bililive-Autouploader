using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BililiveAutoUploader.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BililiveAutoUploader.Tests.Integration;

public sealed class AuthenticationTests(PanelWebApplicationFactory factory) : IClassFixture<PanelWebApplicationFactory>
{
    [Fact]
    public async Task Health_IsAnonymous()
    {
        using var client = CreateClient(allowRedirects: false);

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Panel_RedirectsAnonymousUserToLogin()
    {
        using var client = CreateClient(allowRedirects: false);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task ManagementApi_ReturnsUnauthorizedInsteadOfRedirect()
    {
        using var client = CreateClient(allowRedirects: false);

        var response = await client.GetAsync("/api/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Webhook_RemainsAnonymous()
    {
        using var client = CreateClient(allowRedirects: false);
        var request = new
        {
            EventType = "DeploymentProbe",
            EventTimestamp = DateTimeOffset.UtcNow,
            EventId = Guid.NewGuid().ToString(),
            EventData = new { }
        };

        var response = await client.PostAsJsonAsync("/api/webhooks/bililive", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ValidPassword_CreatesSessionThatCanAccessApi()
    {
        using var client = CreateClient(allowRedirects: false);
        var loginPage = await client.GetAsync("/account/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(token));

        var login = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Password"] = PanelWebApplicationFactory.Password,
            ["RememberMe"] = "false",
            ["ReturnUrl"] = "/",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        var apiResponse = await client.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
    }

    [Fact]
    public async Task InitialPassword_IsOnlyPersistedAsAHash()
    {
        using var scope = factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var setting = await db.ApplicationSettings.SingleAsync(x => x.Key == "Panel.PasswordHash");

        Assert.NotEqual(PanelWebApplicationFactory.Password, setting.Value);
        Assert.DoesNotContain(PanelWebApplicationFactory.Password, setting.Value, StringComparison.Ordinal);
    }

    private HttpClient CreateClient(bool allowRedirects) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = allowRedirects,
        BaseAddress = new Uri("https://localhost")
    });
}

public sealed class PanelWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Password = "integration-test-password";
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "BililiveAutoUploader.Tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dataDirectory, "app.db")}");
        builder.UseSetting("Panel:InitialPassword", Password);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDirectory))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
