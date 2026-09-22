using System.Threading.Channels;
using BililiveAutoUploader.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace BililiveAutoUploader.Application;

public sealed class UploadJobQueue : IUploadJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(jobId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class UploadWorker(
    IUploadJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<UploadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IUploadOrchestrator>();
                await orchestrator.ProcessAsync(jobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "处理上传任务 {JobId} 失败", jobId);
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IUploadJobStore>();
                var job = await store.GetAsync(jobId, stoppingToken).ConfigureAwait(false);
                if (job is not null)
                {
                    job.Status = UploadJobStatus.Failed;
                    job.LastError = ex.Message;
                    job.AttemptCount++;
                    job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(Math.Min(job.AttemptCount * 5, 60));
                    await store.SaveAsync(job, stoppingToken).ConfigureAwait(false);
                }
            }
        }
    }
}

public sealed class QueueRecoveryService(IServiceScopeFactory scopeFactory, IUploadJobQueue queue, ILogger<QueueRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUploadJobStore>();
        var count = 0;
        foreach (var job in await store.GetRunnableAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            var wasRunning = job.Status == UploadJobStatus.Running;
            if (wasRunning)
            {
                job.Status = UploadJobStatus.Pending;
                await store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            }
            await queue.EnqueueAsync(job.Id, cancellationToken).ConfigureAwait(false);
            count++;
        }
        logger.LogInformation("已恢复 {Count} 个待处理上传任务", count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
