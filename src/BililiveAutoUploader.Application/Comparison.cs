using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Application;

public sealed class FileComparisonService(
    StorageOptions options,
    IBaiduPanClient baidu,
    IDbComparisonStore store) : IFileComparisonService
{
    public async Task<ComparisonRun> CompareAsync(CancellationToken cancellationToken)
    {
        var run = new ComparisonRun();
        var local = Directory.Exists(options.LocalRoot)
            ? Directory.EnumerateFiles(options.LocalRoot, "*", SearchOption.AllDirectories).ToDictionary(x => PathSafety.NormalizeRelative(options.LocalRoot, x), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cloud = await ListRecursiveAsync(options.CloudRoot, cancellationToken).ConfigureAwait(false);
        foreach (var path in local.Keys.Union(cloud.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            local.TryGetValue(path, out var localPath);
            cloud.TryGetValue(path, out var cloudEntry);
            var localLength = localPath is null ? (long?)null : new FileInfo(localPath).Length;
            var cloudLength = cloudEntry is null ? (long?)null : cloudEntry.Length;
            var kind = localPath is null ? ComparisonKind.CloudOnly : cloudEntry is null ? ComparisonKind.LocalOnly : localLength == cloudLength ? ComparisonKind.SameSize : ComparisonKind.DifferentSize;
            run.Items.Add(new ComparisonItem { RelativePath = path, LocalLength = localLength, CloudLength = cloudLength, Kind = kind });
        }
        run.CompletedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return run;
    }

    private async Task<Dictionary<string, CloudFileEntry>> ListRecursiveAsync(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, CloudFileEntry>(StringComparer.OrdinalIgnoreCase);
        async Task Walk(string path, string prefix)
        {
            foreach (var entry in await baidu.ListAsync(path, ct).ConfigureAwait(false))
            {
                var relative = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
                if (entry.IsDirectory) await Walk(entry.Path, relative).ConfigureAwait(false); else result[relative] = entry;
            }
        }
        await Walk(root, string.Empty).ConfigureAwait(false);
        return result;
    }
}

public interface IDbComparisonStore
{
    Task SaveAsync(ComparisonRun run, CancellationToken cancellationToken);
}
