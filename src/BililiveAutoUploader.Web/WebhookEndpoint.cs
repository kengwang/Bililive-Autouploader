using System.Text.Json;
using BililiveAutoUploader.Application;
using FastEndpoints;

namespace BililiveAutoUploader.Web;

public sealed class WebhookRequest
{
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset EventTimestamp { get; set; }
    public string EventId { get; set; } = string.Empty;
    public JsonElement EventData { get; set; }
}

public sealed record WebhookResponse(bool Accepted, bool Duplicate, Guid? JobId);

public sealed class WebhookEndpoint(IWebhookProcessor processor) : Endpoint<WebhookRequest, WebhookResponse>
{
    public override void Configure()
    {
        Post("/api/webhooks/bililive");
        AllowAnonymous();
        Summary(s => s.Summary = "接收录播姬 Webhook V2 事件");
    }

    public override async Task HandleAsync(WebhookRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.EventType))
        {
            AddError("EventId 和 EventType 不能为空。");
            await HttpContext.Response.SendAsync(new { error = "EventId 和 EventType 不能为空。" }, 400, cancellation: ct);
            return;
        }
        var result = await processor.ProcessAsync(new WebhookEnvelope(req.EventType, req.EventTimestamp, req.EventId, req.EventData), ct);
        await HttpContext.Response.SendAsync(new WebhookResponse(result.Accepted, result.Duplicate, result.JobId), 202, cancellation: ct);
    }
}
