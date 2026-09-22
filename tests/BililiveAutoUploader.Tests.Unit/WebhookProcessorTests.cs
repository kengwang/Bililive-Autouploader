using System.Text.Json;
using BililiveAutoUploader.Application;
using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Tests.Unit;

public sealed class WebhookProcessorTests
{
    [Fact]
    public async Task DuplicateEvent_IsIgnored()
    {
        var events = new FakeEvents { AlreadyRecorded = false };
        var factory = new FakeFactory();
        var queue = new FakeQueue();
        var processor = new WebhookProcessor(events, factory, queue, new StorageOptions());
        var envelope = new WebhookEnvelope("SessionStarted", DateTimeOffset.UtcNow, "event-1", JsonDocument.Parse("{}").RootElement);
        var first = await processor.ProcessAsync(envelope, CancellationToken.None);
        events.AlreadyRecorded = true;
        var second = await processor.ProcessAsync(envelope, CancellationToken.None);
        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
    }

    private sealed class FakeEvents : IWebhookEventStore
    {
        public bool AlreadyRecorded { get; set; }
        public Task<bool> TryRecordAsync(WebhookEnvelope _, CancellationToken __) => Task.FromResult(!AlreadyRecorded);
        public Task MarkProcessedAsync(string _, CancellationToken __) => Task.CompletedTask;
    }
    private sealed class FakeFactory : IUploadJobFactory
    {
        public Task<UploadJob> CreateFromFileClosedAsync(FileClosedData _, StorageOptions __, CancellationToken ___) => Task.FromResult(new UploadJob());
    }
    private sealed class FakeQueue : IUploadJobQueue
    {
        public ValueTask EnqueueAsync(Guid _, CancellationToken __) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<Guid> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _) { yield break; }
    }
}
