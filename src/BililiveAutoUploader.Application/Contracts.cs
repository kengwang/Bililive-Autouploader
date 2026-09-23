using System.Text.Json;
using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Application;

public sealed record WebhookEnvelope(
    string EventType,
    DateTimeOffset EventTimestamp,
    string EventId,
    JsonElement EventData);

public sealed record FileClosedData(
    string RelativePath,
    long FileSize,
    double Duration,
    DateTimeOffset FileOpenTime,
    DateTimeOffset FileCloseTime,
    Guid SessionId,
    long RoomId,
    long ShortId,
    string Name,
    string Title);

public sealed record JobSummary(
    Guid Id,
    string LocalGroupKey,
    UploadJobStatus Status,
    int AttemptCount,
    long TotalBytes,
    long UploadedBytes,
    double? Speed,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record FileEntryDto(string Path, string Name, bool IsDirectory, long? Length, DateTimeOffset? LastModified);

public sealed record ComparisonItemDto(
    long Id,
    string RelativePath,
    ComparisonKind Kind,
    long? LocalLength,
    long? CloudLength,
    bool Selected,
    bool DeleteLocalAfterUpload);

public sealed record ComparisonEnqueueItem(string RelativePath, bool DeleteLocalAfterUpload);

public sealed record ManualUploadItem(string RelativePath, bool DeleteLocalAfterUpload);

public interface IManualUploadService
{
    Task<IReadOnlyList<Guid>> EnqueueAsync(IReadOnlyList<ManualUploadItem> items, CancellationToken cancellationToken);
}

public interface IComparisonJobService
{
    Task<IReadOnlyList<Guid>> EnqueueAsync(Guid comparisonId, IReadOnlyList<ComparisonEnqueueItem> items, CancellationToken cancellationToken);
}

public interface IWebhookProcessor
{
    Task<WebhookProcessResult> ProcessAsync(WebhookEnvelope envelope, CancellationToken cancellationToken);
}

public sealed record WebhookProcessResult(bool Accepted, bool Duplicate, Guid? JobId);

public interface IUploadJobStore
{
    Task<UploadJob?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<UploadJob>> GetPageAsync(int skip, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<UploadJob>> GetRunnableAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task AddAsync(UploadJob job, CancellationToken cancellationToken);
    Task SaveAsync(UploadJob job, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public interface IUploadJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

public interface IUploadOrchestrator
{
    Task ProcessAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface ILocalFileService
{
    Task<IReadOnlyList<FileEntryDto>> ListAsync(string? relativePath, CancellationToken cancellationToken);
    Task<bool> WaitForStableAsync(string path, TimeSpan timeout, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken, bool recursive = false);
}

public interface IBaiduPanClient
{
    Task<IReadOnlyList<CloudFileEntry>> ListAsync(string path, CancellationToken cancellationToken);
    Task<CloudFileEntry?> GetMetadataAsync(string path, CancellationToken cancellationToken);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<bool> TryRapidUploadAsync(string localPath, string cloudPath, FileChecksum checksum, CancellationToken cancellationToken);
    Task UploadAsync(string localPath, string cloudPath, FileChecksum checksum, IProgress<UploadProgress>? progress, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

public sealed record CloudFileEntry(string Path, string Name, bool IsDirectory, long Length, string? Md5);
public sealed record FileChecksum(long Length, string Md5, string SliceMd5, string Crc32, IReadOnlyList<string> BlockMd5);
public sealed record UploadProgress(long UploadedBytes, long TotalBytes, int PartIndex, int PartCount);

public interface IFileChecksumService
{
    Task<FileChecksum> CalculateAsync(string path, CancellationToken cancellationToken);
}

public interface IFileComparisonService
{
    Task<ComparisonRun> CompareAsync(CancellationToken cancellationToken);
}

public sealed class StorageOptions
{
    public string LocalRoot { get; set; } = "/recordings";
    public string CloudRoot { get; set; } = "/alist/bililive";
    public int MaxParallelJobs { get; set; } = 2;
    public int PartParallelism { get; set; } = 4;
    public TimeSpan SidecarWait { get; set; } = TimeSpan.FromSeconds(15);
    public int StableChecks { get; set; } = 2;
    public TimeSpan StableCheckInterval { get; set; } = TimeSpan.FromSeconds(2);
    public string SidecarExtensionsCsv { get; set; } = ".xml,.ass,.danmaku";
}

public sealed class BaiduOptions
{
    public string BaseAddress { get; set; } = "https://pan.baidu.com";
    public string PcsAddress { get; set; } = "https://d.pcs.baidu.com";
    public string Cookie { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int AppId { get; set; } = 250528;
    public int ChunkSizeMiB { get; set; } = 16;
    public int MaxRetries { get; set; } = 3;
}
