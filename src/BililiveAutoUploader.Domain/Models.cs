namespace BililiveAutoUploader.Domain;

public enum UploadJobStatus
{
    Pending,
    WaitingForSidecars,
    Ready,
    Running,
    Paused,
    Succeeded,
    Failed,
    Canceled,
    DeletePending,
    DeleteFailed
}

public enum ComparisonKind
{
    LocalOnly,
    CloudOnly,
    SameSize,
    DifferentSize,
    HashEqual,
    HashDifferent,
    Error
}

public sealed class WebhookEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset EventTimestamp { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Processed { get; set; }
}

public sealed class UploadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LocalGroupKey { get; set; } = string.Empty;
    public UploadJobStatus Status { get; set; } = UploadJobStatus.Pending;
    public bool DeleteLocalAfterSuccess { get; set; } = true;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
    public long TotalBytes { get; set; }
    public long UploadedBytes { get; set; }
    public double? UploadSpeedBytesPerSecond { get; set; }
    public ICollection<UploadJobItem> Items { get; set; } = new List<UploadJobItem>();
    public ICollection<UploadJobLog> Logs { get; set; } = new List<UploadJobLog>();
}

public sealed class UploadJobItem
{
    public long Id { get; set; }
    public Guid UploadJobId { get; set; }
    public UploadJob? UploadJob { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string CloudPath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string? Md5 { get; set; }
    public string? SliceMd5 { get; set; }
    public string? Crc32 { get; set; }
    public bool IsSidecar { get; set; }
    public bool DeleteAfterSuccess { get; set; } = true;
    public bool Uploaded { get; set; }
    public bool Deleted { get; set; }
}

public sealed class UploadJobLog
{
    public long Id { get; set; }
    public Guid UploadJobId { get; set; }
    public UploadJob? UploadJob { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = string.Empty;
}

public sealed class ComparisonRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public ICollection<ComparisonItem> Items { get; set; } = new List<ComparisonItem>();
}

public sealed class ComparisonItem
{
    public long Id { get; set; }
    public Guid ComparisonRunId { get; set; }
    public ComparisonRun? ComparisonRun { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long? LocalLength { get; set; }
    public long? CloudLength { get; set; }
    public string? LocalHash { get; set; }
    public string? CloudHash { get; set; }
    public ComparisonKind Kind { get; set; }
    public bool Selected { get; set; }
    public bool DeleteLocalAfterUpload { get; set; }
}

public sealed class UploadRule
{
    public long Id { get; set; }
    public string Name { get; set; } = "Default";
    public string PrimaryExtensionsCsv { get; set; } = ".flv,.mp4";
    public string SidecarExtensionsCsv { get; set; } = ".xml,.ass,.danmaku";
    public TimeSpan SidecarWait { get; set; } = TimeSpan.FromSeconds(15);
    public bool DeleteLocalAfterSuccess { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public sealed class ApplicationSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
