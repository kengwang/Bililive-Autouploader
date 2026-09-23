using System.Net;
using System.Text;
using BililiveAutoUploader.Application;
using BililiveAutoUploader.Infrastructure;

namespace BililiveAutoUploader.Tests.Unit;

public sealed class BaiduPanClientTests
{
    [Fact]
    public async Task InvalidUtf8CharsetOnErrorResponse_IsReportedAsBaiduHttpError()
    {
        var content = new StringContent("{\"errno\":-6}", Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "utf8";
        var client = new BaiduPanClient(
            new HttpClient(new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = content
            })),
            new BaiduOptions());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("/", CancellationToken.None));

        Assert.Contains("-6", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage _, CancellationToken __)
            => Task.FromResult(response);
    }
}
