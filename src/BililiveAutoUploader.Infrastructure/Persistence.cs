using BililiveAutoUploader.Application;
using BililiveAutoUploader.Domain;
using Microsoft.EntityFrameworkCore;

namespace BililiveAutoUploader.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<UploadJob> UploadJobs => Set<UploadJob>();
    public DbSet<UploadJobItem> UploadJobItems => Set<UploadJobItem>();
    public DbSet<UploadJobLog> UploadJobLogs => Set<UploadJobLog>();
    public DbSet<ComparisonRun> ComparisonRuns => Set<ComparisonRun>();
    public DbSet<ComparisonItem> ComparisonItems => Set<ComparisonItem>();
    public DbSet<UploadRule> UploadRules => Set<UploadRule>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<WebhookEvent>().HasIndex(x => x.EventId).IsUnique();
        modelBuilder.Entity<UploadJob>().HasKey(x => x.Id);
        modelBuilder.Entity<UploadJob>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        modelBuilder.Entity<UploadJob>().HasIndex(x => x.LocalGroupKey);
        modelBuilder.Entity<UploadJobItem>().HasIndex(x => x.LocalPath);
        modelBuilder.Entity<ComparisonItem>().HasIndex(x => new { x.ComparisonRunId, x.RelativePath });
        modelBuilder.Entity<ApplicationSetting>().HasKey(x => x.Key);
        modelBuilder.Entity<UploadJob>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<ComparisonItem>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<UploadJob>().HasMany(x => x.Items).WithOne(x => x.UploadJob).HasForeignKey(x => x.UploadJobId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UploadJob>().HasMany(x => x.Logs).WithOne(x => x.UploadJob).HasForeignKey(x => x.UploadJobId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ComparisonRun>().HasMany(x => x.Items).WithOne(x => x.ComparisonRun).HasForeignKey(x => x.ComparisonRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EfWebhookEventStore(IDbContextFactory<AppDbContext> factory) : IWebhookEventStore
{
    public async Task<bool> TryRecordAsync(WebhookEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.WebhookEvents.AnyAsync(x => x.EventId == envelope.EventId, cancellationToken).ConfigureAwait(false))
            return false;
        var json = System.Text.Json.JsonSerializer.Serialize(envelope, JsonOptions.Default);
        db.WebhookEvents.Add(new WebhookEvent { EventId = envelope.EventId, EventType = envelope.EventType, EventTimestamp = envelope.EventTimestamp, PayloadJson = json });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task MarkProcessedAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.WebhookEvents.SingleOrDefaultAsync(x => x.EventId == eventId, cancellationToken).ConfigureAwait(false);
        if (entity is null) return;
        entity.Processed = true;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EfUploadJobStore(IDbContextFactory<AppDbContext> factory) : IUploadJobStore
{
    public async Task<UploadJob?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.UploadJobs.Include(x => x.Items).Include(x => x.Logs).SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UploadJob>> GetPageAsync(int skip, int take, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await db.UploadJobs.Include(x => x.Items).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return jobs.OrderByDescending(x => x.CreatedAt).Skip(skip).Take(take).ToList();
    }

    public async Task<IReadOnlyList<UploadJob>> GetRunnableAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await db.UploadJobs.Where(x => x.Status == UploadJobStatus.Pending || x.Status == UploadJobStatus.Ready || x.Status == UploadJobStatus.Running || x.Status == UploadJobStatus.Failed).ToListAsync(cancellationToken).ConfigureAwait(false);
        return jobs.Where(x => x.Status != UploadJobStatus.Failed || x.NextAttemptAt is null || x.NextAttemptAt <= now).ToList();
    }

    public async Task AddAsync(UploadJob job, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.UploadJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(UploadJob job, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.UploadJobs.Update(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.UploadJobs.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (job is null) return;
        db.UploadJobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EfUploadJobFactory(IUploadJobStore store) : IUploadJobFactory
{
    public async Task<UploadJob> CreateFromFileClosedAsync(FileClosedData data, StorageOptions options, CancellationToken cancellationToken)
    {
        var root = options.LocalRoot;
        var fullPath = PathSafety.ResolveUnderRoot(root, data.RelativePath);
        var relative = data.RelativePath.Replace('\\', '/');
        var groupKey = Path.GetDirectoryName(relative)?.Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(relative);
        var job = new UploadJob { LocalGroupKey = groupKey, Status = UploadJobStatus.WaitingForSidecars, DeleteLocalAfterSuccess = true };
        var cloudPath = (options.CloudRoot.TrimEnd('/') + "/" + relative).Replace("//", "/");
        job.Items.Add(new UploadJobItem { LocalPath = fullPath, RelativePath = relative, CloudPath = cloudPath, Length = data.FileSize, IsSidecar = false });

        var directory = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        foreach (var extension in options.SidecarExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sidecar = Path.Combine(directory, stem + extension);
            if (File.Exists(sidecar))
            {
                var sidecarRelative = PathSafety.NormalizeRelative(root, sidecar);
                job.Items.Add(new UploadJobItem { LocalPath = sidecar, RelativePath = sidecarRelative, CloudPath = (options.CloudRoot.TrimEnd('/') + "/" + sidecarRelative).Replace("//", "/"), Length = new FileInfo(sidecar).Length, IsSidecar = true, DeleteAfterSuccess = UploadFilePolicy.ShouldDeleteAfterUpload(sidecarRelative, job.DeleteLocalAfterSuccess) });
            }
        }
        job.Status = UploadJobStatus.Ready;
        await store.AddAsync(job, cancellationToken).ConfigureAwait(false);
        return job;
    }
}
