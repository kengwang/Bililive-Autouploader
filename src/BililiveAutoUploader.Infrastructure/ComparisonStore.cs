using BililiveAutoUploader.Application;
using BililiveAutoUploader.Domain;
using Microsoft.EntityFrameworkCore;

namespace BililiveAutoUploader.Infrastructure;

public sealed class EfComparisonStore(IDbContextFactory<AppDbContext> factory) : IDbComparisonStore
{
    public async Task SaveAsync(ComparisonRun run, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ComparisonRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EfManualUploadService(
    IUploadJobStore jobs,
    IUploadJobQueue queue,
    StorageOptions options) : IManualUploadService
{
    public async Task<IReadOnlyList<Guid>> EnqueueAsync(IReadOnlyList<ManualUploadItem> selections, CancellationToken cancellationToken)
    {
        if (selections.Count == 0) return [];

        var selectedPaths = selections
            .Where(x => !string.IsNullOrWhiteSpace(x.RelativePath))
            .GroupBy(x => x.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().DeleteLocalAfterUpload, StringComparer.OrdinalIgnoreCase);
        var jobsToQueue = new List<UploadJob>();
        foreach (var group in selectedPaths
            .Select(x => new { RelativePath = x.Key, Delete = x.Value })
            .GroupBy(x => GroupKey(x.RelativePath), StringComparer.OrdinalIgnoreCase))
        {
            var job = new UploadJob
            {
                LocalGroupKey = group.Key,
                Status = UploadJobStatus.Ready,
                DeleteLocalAfterSuccess = group.Any(x => x.Delete)
            };
            foreach (var selected in group)
            {
                var relative = selected.RelativePath.Replace('\\', '/');
                var localPath = PathSafety.ResolveUnderRoot(options.LocalRoot, relative);
                if (!File.Exists(localPath)) continue;
                job.Items.Add(new UploadJobItem
                {
                    LocalPath = localPath,
                    RelativePath = relative,
                    CloudPath = (options.CloudRoot.TrimEnd('/') + "/" + relative).Replace("//", "/"),
                    Length = new FileInfo(localPath).Length,
                    IsSidecar = IsSidecar(relative),
                    DeleteAfterSuccess = selected.Delete
                });
            }
            if (job.Items.Count > 0) jobsToQueue.Add(job);
        }

        foreach (var job in jobsToQueue)
        {
            await jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            await queue.EnqueueAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        return jobsToQueue.Select(x => x.Id).ToArray();
    }

    private string GroupKey(string relative)
    {
        var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(relative);
        return string.IsNullOrEmpty(directory) ? stem : directory + "/" + stem;
    }

    private bool IsSidecar(string relative)
        => options.SidecarExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => relative.EndsWith(x, StringComparison.OrdinalIgnoreCase));
}

public sealed class EfComparisonJobService(
    IDbContextFactory<AppDbContext> factory,
    IManualUploadService manualUploads) : IComparisonJobService
{
    public async Task<IReadOnlyList<Guid>> EnqueueAsync(Guid comparisonId, IReadOnlyList<ComparisonEnqueueItem> selections, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var requested = selections.Select(x => x.RelativePath).ToArray();
        var rows = await db.ComparisonItems
            .Where(x => x.ComparisonRunId == comparisonId && requested.Contains(x.RelativePath) && x.LocalLength != null)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var deleteByPath = selections.ToDictionary(x => x.RelativePath, x => x.DeleteLocalAfterUpload, StringComparer.OrdinalIgnoreCase);
        return await manualUploads.EnqueueAsync(rows.Select(x => new ManualUploadItem(x.RelativePath, deleteByPath[x.RelativePath])).ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
