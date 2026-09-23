using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Application;

public sealed class FileComparisonService(
    StorageOptions options,
    IBaiduPanClient baidu,
    IDbComparisonStore store) : IFileComparisonService
{
    private readonly object _progressLock = new();
    private ComparisonProgress _progress = new(false, "Idle", string.Empty, 0, 0, 0, null, null, null);

    public ComparisonProgress GetProgress()
    {
        lock (_progressLock) return _progress;
    }

    public async Task<ComparisonRun> CompareAsync(CancellationToken cancellationToken)
    {
        lock (_progressLock)
        {
            if (_progress.IsRunning) throw new InvalidOperationException("全量扫描正在进行中。");
            _progress = new(true, "Local", string.Empty, 0, 0, 0, DateTimeOffset.UtcNow, null, null);
        }
        var run = new ComparisonRun();
        try
        {
            var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(options.LocalRoot))
            {
                foreach (var path in Directory.EnumerateFiles(options.LocalRoot, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    local[PathSafety.NormalizeRelative(options.LocalRoot, path)] = path;
                    UpdateProgress(p => p with { CurrentPath = PathSafety.NormalizeRelative(options.LocalRoot, path), LocalFilesScanned = p.LocalFilesScanned + 1 });
                }
            }
            UpdateProgress(p => p with { Phase = "Cloud", CurrentPath = options.CloudRoot });
            var cloud = await ListRecursiveAsync(options.CloudRoot, cancellationToken).ConfigureAwait(false);
            foreach (var path in local.Keys.Union(cloud.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                local.TryGetValue(path, out var localPath);
                cloud.TryGetValue(path, out var cloudEntry);
                var localLength = localPath is null ? (long?)null : new FileInfo(localPath).Length;
                var cloudLength = cloudEntry is null ? (long?)null : cloudEntry.Length;
                var kind = localPath is null ? ComparisonKind.CloudOnly : cloudEntry is null ? ComparisonKind.LocalOnly : localLength == cloudLength ? ComparisonKind.SameSize : ComparisonKind.DifferentSize;
                run.Items.Add(new ComparisonItem { RelativePath = path, LocalLength = localLength, CloudLength = cloudLength, Kind = kind });
                UpdateProgress(p => p with { ResultCount = run.Items.Count });
            }
            run.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
            UpdateProgress(p => p with { IsRunning = false, Phase = "Completed", CurrentPath = string.Empty, CompletedAt = run.CompletedAt, ResultCount = run.Items.Count });
            return run;
        }
        catch (Exception ex)
        {
            UpdateProgress(p => p with { IsRunning = false, Phase = "Failed", CompletedAt = DateTimeOffset.UtcNow, Error = ex.Message });
            throw;
        }
    }

    private async Task<Dictionary<string, CloudFileEntry>> ListRecursiveAsync(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, CloudFileEntry>(StringComparer.OrdinalIgnoreCase);
        async Task Walk(string path, string prefix)
        {
            foreach (var entry in await baidu.ListAsync(path, ct).ConfigureAwait(false))
            {
                var relative = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
                UpdateProgress(p => p with { CurrentPath = relative });
                if (entry.IsDirectory) await Walk(entry.Path, relative).ConfigureAwait(false); else { result[relative] = entry; UpdateProgress(p => p with { CloudFilesScanned = p.CloudFilesScanned + 1 }); }
            }
        }
        await Walk(root, string.Empty).ConfigureAwait(false);
        return result;
    }

    private void UpdateProgress(Func<ComparisonProgress, ComparisonProgress> update)
    {
        lock (_progressLock) _progress = update(_progress);
    }
}

public interface IDbComparisonStore
{
    Task SaveAsync(ComparisonRun run, CancellationToken cancellationToken);
}
