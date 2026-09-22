using BililiveAutoUploader.Domain;
using Microsoft.Extensions.Logging;

namespace BililiveAutoUploader.Application;

public sealed class UploadOrchestrator(
    IUploadJobStore store,
    ILocalFileService localFiles,
    IFileChecksumService checksums,
    IBaiduPanClient baidu,
    StorageOptions options,
    ILogger<UploadOrchestrator> logger) : IUploadOrchestrator
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.MaxParallelJobs), Math.Max(1, options.MaxParallelJobs));

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"任务 {jobId} 不存在。");
            if (job.Status is UploadJobStatus.Paused or UploadJobStatus.Canceled or UploadJobStatus.Succeeded) return;
            if (job.Status == UploadJobStatus.WaitingForSidecars || job.Items.Count > 0)
            {
                await Task.Delay(options.SidecarWait, cancellationToken).ConfigureAwait(false);
                AddStableSidecars(job);
            }
            job.Status = UploadJobStatus.Running;
            job.StartedAt ??= DateTimeOffset.UtcNow;
            await store.SaveAsync(job, cancellationToken).ConfigureAwait(false);

            foreach (var item in job.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Uploaded) continue;
                if (!await localFiles.WaitForStableAsync(item.LocalPath, options.SidecarWait, cancellationToken).ConfigureAwait(false))
                    throw new IOException($"文件在等待窗口内未稳定: {item.RelativePath}");
                var checksum = await checksums.CalculateAsync(item.LocalPath, cancellationToken).ConfigureAwait(false);
                item.Length = checksum.Length;
                item.Md5 = checksum.Md5;
                item.SliceMd5 = checksum.SliceMd5;
                item.Crc32 = checksum.Crc32;
                var rapid = await baidu.TryRapidUploadAsync(item.LocalPath, item.CloudPath, checksum, cancellationToken).ConfigureAwait(false);
                if (!rapid)
                {
                    var progress = new Progress<UploadProgress>(p =>
                    {
                        job.UploadedBytes = job.Items.Where(x => x.Uploaded).Sum(x => x.Length) + p.UploadedBytes;
                        job.TotalBytes = job.Items.Sum(x => x.Length);
                        job.UploadSpeedBytesPerSecond = p.UploadedBytes / Math.Max(1, (DateTimeOffset.UtcNow - job.StartedAt!.Value).TotalSeconds);
                    });
                    await baidu.UploadAsync(item.LocalPath, item.CloudPath, checksum, progress, cancellationToken).ConfigureAwait(false);
                }
                var remote = await baidu.GetMetadataAsync(item.CloudPath, cancellationToken).ConfigureAwait(false);
                if (remote is null || remote.Length != checksum.Length)
                    throw new IOException($"云端校验失败: {item.CloudPath}");
                item.Uploaded = true;
                job.UploadedBytes = job.Items.Where(x => x.Uploaded).Sum(x => x.Length);
                await store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            }

            job.TotalBytes = job.Items.Sum(x => x.Length);
            if (job.DeleteLocalAfterSuccess)
            {
                job.Status = UploadJobStatus.DeletePending;
                await store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                foreach (var item in job.Items.Where(x => x.DeleteAfterSuccess && !x.Deleted))
                {
                    try
                    {
                        await localFiles.DeleteAsync(item.LocalPath, cancellationToken).ConfigureAwait(false);
                        item.Deleted = true;
                    }
                    catch (Exception ex)
                    {
                        job.Status = UploadJobStatus.DeleteFailed;
                        job.LastError = ex.Message;
                        logger.LogWarning(ex, "删除本地文件失败 {Path}", item.LocalPath);
                    }
                }
            }
            job.Status = job.Status == UploadJobStatus.DeleteFailed ? UploadJobStatus.DeleteFailed : UploadJobStatus.Succeeded;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void AddStableSidecars(UploadJob job)
    {
        var primary = job.Items.FirstOrDefault(x => !x.IsSidecar);
        if (primary is null) return;
        var directory = Path.GetDirectoryName(primary.LocalPath);
        var stem = Path.GetFileNameWithoutExtension(primary.LocalPath);
        if (directory is null) return;
        foreach (var extension in options.SidecarExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory, stem + extension);
            if (!File.Exists(path) || job.Items.Any(x => string.Equals(x.LocalPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var relative = PathSafety.NormalizeRelative(options.LocalRoot, path);
            job.Items.Add(new UploadJobItem
            {
                LocalPath = path,
                RelativePath = relative,
                CloudPath = (options.CloudRoot.TrimEnd('/') + "/" + relative).Replace("//", "/"),
                Length = new FileInfo(path).Length,
                IsSidecar = true,
                DeleteAfterSuccess = job.DeleteLocalAfterSuccess
            });
        }
    }
}
