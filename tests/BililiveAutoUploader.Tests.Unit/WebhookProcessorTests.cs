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

    [Fact]
    public async Task FileClosed_ForConfiguredSidecar_DoesNotCreateStandaloneJob()
    {
        var events = new FakeEvents();
        var factory = new FakeFactory();
        var queue = new FakeQueue();
        var processor = new WebhookProcessor(events, factory, queue, new StorageOptions());
        var data = new { RelativePath = "room/day/recording.xml", FileSize = 10, Duration = 0d,
            FileOpenTime = DateTimeOffset.UtcNow, FileCloseTime = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid(), RoomId = 1L, ShortId = 1L, Name = "recording.xml", Title = "test" };
        var envelope = new WebhookEnvelope("FileClosed", DateTimeOffset.UtcNow, "sidecar-1",
            JsonSerializer.SerializeToElement(data));

        var result = await processor.ProcessAsync(envelope, CancellationToken.None);

        Assert.Null(result.JobId);
        Assert.Empty(queue.Enqueued);
        Assert.Equal(0, factory.Created);
    }

    private sealed class FakeEvents : IWebhookEventStore
    {
        public bool AlreadyRecorded { get; set; }
        public Task<bool> TryRecordAsync(WebhookEnvelope _, CancellationToken __) => Task.FromResult(!AlreadyRecorded);
        public Task MarkProcessedAsync(string _, CancellationToken __) => Task.CompletedTask;
    }
    private sealed class FakeFactory : IUploadJobFactory
    {
        public int Created { get; private set; }
        public Task<UploadJob> CreateFromFileClosedAsync(FileClosedData _, StorageOptions __, CancellationToken ___) { Created++; return Task.FromResult(new UploadJob()); }
    }
    private sealed class FakeQueue : IUploadJobQueue
    {
        public List<Guid> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(Guid id, CancellationToken __) { Enqueued.Add(id); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<Guid> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _) { yield break; }
    }
}
