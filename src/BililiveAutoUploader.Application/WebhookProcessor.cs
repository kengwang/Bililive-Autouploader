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
        // 录播姬会为 XML、封面等附属文件也发送 FileClosed。它们必须并入同目录的主录播任务，
        // 不能各自创建任务，否则会和主文件任务竞争同一个 sidecar 并产生“未稳定/不存在”。
        if (IsConfiguredSidecar(closed.RelativePath, options.SidecarExtensionsCsv))
        {
            await eventStore.MarkProcessedAsync(envelope.EventId, cancellationToken).ConfigureAwait(false);
            return new(true, false, null);
        }
        var job = await jobFactory.CreateFromFileClosedAsync(closed, options, cancellationToken).ConfigureAwait(false);
        await queue.EnqueueAsync(job.Id, cancellationToken).ConfigureAwait(false);
        await eventStore.MarkProcessedAsync(envelope.EventId, cancellationToken).ConfigureAwait(false);
        return new(true, false, job.Id);
    }

    private static bool IsConfiguredSidecar(string relativePath, string extensionsCsv)
    {
        var normalized = relativePath.Replace('\\', '/');
        return extensionsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(extension => normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
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
