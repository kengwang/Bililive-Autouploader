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
            var selectedByPath = group.ToDictionary(x => x.RelativePath, x => x.Delete, StringComparer.OrdinalIgnoreCase);
            // 手动同步也遵循录播文件组规则：选中 XML 或 .cover.jpg 时，自动并入同基名主文件
            // 和已有白名单附属文件，避免每个 sidecar 变成独立任务。
            var primary = FindPrimaryPath(group.Key);
            if (primary is not null && !selectedByPath.ContainsKey(primary))
                selectedByPath[primary] = group.First().Delete;
            foreach (var sidecar in FindExistingSidecars(group.Key))
                if (!selectedByPath.ContainsKey(sidecar)) selectedByPath[sidecar] = group.First().Delete;

            foreach (var selected in selectedByPath)
            {
                var relative = selected.Key.Replace('\\', '/');
                var localPath = PathSafety.ResolveUnderRoot(options.LocalRoot, relative);
                if (!File.Exists(localPath)) continue;
                job.Items.Add(new UploadJobItem
                {
                    LocalPath = localPath,
                    RelativePath = relative,
                    CloudPath = (options.CloudRoot.TrimEnd('/') + "/" + relative).Replace("//", "/"),
                    Length = new FileInfo(localPath).Length,
                    IsSidecar = IsSidecar(relative),
                    DeleteAfterSuccess = UploadFilePolicy.ShouldDeleteAfterUpload(relative, selected.Value)
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
        var stem = Path.GetFileName(relative);
        foreach (var extension in options.SidecarExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .OrderByDescending(x => x.Length))
        {
            if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^extension.Length];
                break;
            }
        }
        if (stem.Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase))
            stem = Path.GetFileNameWithoutExtension(relative);
        return string.IsNullOrEmpty(directory) ? stem : directory + "/" + stem;
    }

    private string? FindPrimaryPath(string groupKey)
    {
        var directory = Path.GetDirectoryName(groupKey)?.Replace('\\', '/') ?? string.Empty;
        var stem = Path.GetFileName(groupKey);
        var directoryPath = PathSafety.ResolveUnderRoot(options.LocalRoot, directory);
        if (!Directory.Exists(directoryPath)) return null;
        var candidates = Directory.EnumerateFiles(directoryPath, stem + ".*", SearchOption.TopDirectoryOnly);
        return candidates.Select(x => PathSafety.NormalizeRelative(options.LocalRoot, x))
            .FirstOrDefault(x => !IsSidecar(x));
    }

    private IEnumerable<string> FindExistingSidecars(string groupKey)
    {
        var directory = Path.GetDirectoryName(groupKey)?.Replace('\\', '/') ?? string.Empty;
        var stem = Path.GetFileName(groupKey);
        foreach (var extension in options.SidecarExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var relative = string.IsNullOrEmpty(directory) ? stem + extension : directory + "/" + stem + extension;
            var path = PathSafety.ResolveUnderRoot(options.LocalRoot, relative);
            if (File.Exists(path)) yield return relative;
        }
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
