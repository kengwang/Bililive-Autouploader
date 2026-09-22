using System.Text.Json;
using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Application;

public sealed class WebhookProcessor(
    IWebhookEventStore eventStore,
    IUploadJobFactory jobFactory,
    IUploadJobQueue queue,
    StorageOptions options) : IWebhookProcessor
{
    public async Task<WebhookProcessResult> ProcessAsync(WebhookEnvelope envelope, CancellationToken cancellationToken)
    {
        var recorded = await eventStore.TryRecordAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (!recorded)
            return new(true, true, null);

        if (!string.Equals(envelope.EventType, "FileClosed", StringComparison.OrdinalIgnoreCase))
            return new(true, false, null);

        var closed = envelope.EventData.Deserialize<FileClosedData>(JsonOptions.Default)
            ?? throw new InvalidOperationException("FileClosed EventData 无效。");
        var job = await jobFactory.CreateFromFileClosedAsync(closed, options, cancellationToken).ConfigureAwait(false);
        await queue.EnqueueAsync(job.Id, cancellationToken).ConfigureAwait(false);
        await eventStore.MarkProcessedAsync(envelope.EventId, cancellationToken).ConfigureAwait(false);
        return new(true, false, job.Id);
    }
}

public interface IWebhookEventStore
{
    Task<bool> TryRecordAsync(WebhookEnvelope envelope, CancellationToken cancellationToken);
    Task MarkProcessedAsync(string eventId, CancellationToken cancellationToken);
}

public interface IUploadJobFactory
{
    Task<UploadJob> CreateFromFileClosedAsync(FileClosedData data, StorageOptions options, CancellationToken cancellationToken);
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
