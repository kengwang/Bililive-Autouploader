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
