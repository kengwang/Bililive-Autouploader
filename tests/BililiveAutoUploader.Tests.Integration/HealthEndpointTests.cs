using Microsoft.AspNetCore.Mvc.Testing;

namespace BililiveAutoUploader.Tests.Integration;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }
}
