using System.Threading.RateLimiting;
using BililiveAutoUploader.Application;
using BililiveAutoUploader.Domain;
using BililiveAutoUploader.Infrastructure;
using BililiveAutoUploader.Web.Authentication;
using FastEndpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
var baidu = builder.Configuration.GetSection("Baidu").Get<BaiduOptions>() ?? new BaiduOptions();
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton(baidu);
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/app.db"));
builder.Services.AddHttpClient<BaiduPanClient>();
builder.Services.AddFastEndpoints();
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.Configure<PanelAuthenticationOptions>(builder.Configuration.GetSection("Panel"));
builder.Services.AddSingleton<IPasswordHasher<PanelUser>, PasswordHasher<PanelUser>>();
builder.Services.AddScoped<IPanelCredentialService, PanelCredentialService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "BililiveAutoUploader.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Kestrel is only reachable from the two private Docker networks in production.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton<IUploadJobQueue, UploadJobQueue>();
builder.Services.AddScoped<IWebhookProcessor, WebhookProcessor>();
builder.Services.AddScoped<IWebhookEventStore, EfWebhookEventStore>();
builder.Services.AddScoped<IUploadJobStore, EfUploadJobStore>();
builder.Services.AddScoped<IUploadJobFactory, EfUploadJobFactory>();
builder.Services.AddScoped<IDbComparisonStore, EfComparisonStore>();
builder.Services.AddScoped<IFileComparisonService, FileComparisonService>();
builder.Services.AddScoped<IManualUploadService, EfManualUploadService>();
builder.Services.AddScoped<IComparisonJobService, EfComparisonJobService>();
builder.Services.AddScoped<IUploadOrchestrator, UploadOrchestrator>();
builder.Services.AddSingleton<ILocalFileService, LocalFileService>();
builder.Services.AddSingleton<IFileChecksumService, FileChecksumService>();
builder.Services.AddSingleton<IBaiduPanClient>(sp => sp.GetRequiredService<BaiduPanClient>());
builder.Services.AddHostedService<UploadWorker>();
builder.Services.AddHostedService<QueueRecoveryService>();

var app = builder.Build();
Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "data"));
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<IPanelCredentialService>()
        .EnsureInitializedAsync(CancellationToken.None);
}
app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseFastEndpoints();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow })).AllowAnonymous();
app.MapGet("/api/jobs", async (int? skip, int? take, IUploadJobStore store, CancellationToken ct) => Results.Ok((await store.GetPageAsync(Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 200), ct)).Select(j => new JobSummary(j.Id, j.LocalGroupKey, j.Status, j.AttemptCount, j.TotalBytes, j.UploadedBytes, j.UploadSpeedBytesPerSecond, j.LastError, j.CreatedAt, j.CompletedAt))));
app.MapGet("/api/jobs/{id:guid}", async (Guid id, IUploadJobStore store, CancellationToken ct) => { var job = await store.GetAsync(id, ct); return job is null ? Results.NotFound() : Results.Ok(job); });
app.MapPost("/api/jobs/{id:guid}/retry", async (Guid id, IUploadJobStore store, IUploadJobQueue queue, CancellationToken ct) => { var job = await store.GetAsync(id, ct); if (job is null) return Results.NotFound(); job.Status = UploadJobStatus.Pending; job.NextAttemptAt = null; job.LastError = null; await store.SaveAsync(job, ct); await queue.EnqueueAsync(id, ct); return Results.Accepted($"/api/jobs/{id}"); });
app.MapPost("/api/jobs/{id:guid}/pause", async (Guid id, IUploadJobStore store, CancellationToken ct) => { var job = await store.GetAsync(id, ct); if (job is null) return Results.NotFound(); job.Status = UploadJobStatus.Paused; await store.SaveAsync(job, ct); return Results.Ok(); });
app.MapPost("/api/jobs/{id:guid}/resume", async (Guid id, IUploadJobStore store, IUploadJobQueue queue, CancellationToken ct) => { var job = await store.GetAsync(id, ct); if (job is null) return Results.NotFound(); job.Status = UploadJobStatus.Pending; await store.SaveAsync(job, ct); await queue.EnqueueAsync(id, ct); return Results.Accepted($"/api/jobs/{id}"); });
app.MapPost("/api/jobs/{id:guid}/cancel", async (Guid id, IUploadJobStore store, CancellationToken ct) => { var job = await store.GetAsync(id, ct); if (job is null) return Results.NotFound(); job.Status = UploadJobStatus.Canceled; await store.SaveAsync(job, ct); return Results.Ok(); });
app.MapDelete("/api/jobs/{id:guid}", async (Guid id, IUploadJobStore store, CancellationToken ct) => { await store.DeleteAsync(id, ct); return Results.NoContent(); });
app.MapGet("/api/files/local", async (string? path, ILocalFileService files, CancellationToken ct) => Results.Ok(await files.ListAsync(path, ct)));
app.MapGet("/api/files/cloud", async (string? path, IBaiduPanClient client, CancellationToken ct) => Results.Ok(await client.ListAsync(path ?? "/", ct)));
app.MapPost("/api/comparisons", async (IFileComparisonService comparison, CancellationToken ct) => Results.Ok(await comparison.CompareAsync(ct)));
app.MapPost("/api/comparisons/{id:guid}/enqueue", async (Guid id, ComparisonEnqueueItem[] items, IComparisonJobService service, CancellationToken ct) => Results.Accepted($"/api/jobs", await service.EnqueueAsync(id, items, ct)));
app.MapPost("/api/uploads", async (ManualUploadItem[] items, IManualUploadService service, CancellationToken ct) => Results.Accepted($"/api/jobs", await service.EnqueueAsync(items, ct)));
app.MapGet("/api/settings/upload", ([Microsoft.AspNetCore.Mvc.FromServices] StorageOptions settings) => Results.Ok(settings));
app.MapPut("/api/settings/upload", (StorageOptions incoming, [Microsoft.AspNetCore.Mvc.FromServices] StorageOptions settings) => { settings.LocalRoot = incoming.LocalRoot; settings.CloudRoot = incoming.CloudRoot; settings.MaxParallelJobs = Math.Max(1, incoming.MaxParallelJobs); settings.PartParallelism = Math.Max(1, incoming.PartParallelism); settings.SidecarWait = incoming.SidecarWait; settings.StableChecks = Math.Max(1, incoming.StableChecks); settings.StableCheckInterval = incoming.StableCheckInterval; settings.SidecarExtensionsCsv = incoming.SidecarExtensionsCsv; return Results.Ok(settings); });
app.MapGet("/api/settings/baidu", ([Microsoft.AspNetCore.Mvc.FromServices] BaiduOptions settings) => Results.Ok(new { settings.BaseAddress, settings.PcsAddress, settings.AppId, settings.ChunkSizeMiB, settings.MaxRetries, HasCookie = !string.IsNullOrWhiteSpace(settings.Cookie), HasAccessToken = !string.IsNullOrWhiteSpace(settings.AccessToken) }));
app.MapPut("/api/settings/baidu", (BaiduOptions incoming, [Microsoft.AspNetCore.Mvc.FromServices] BaiduOptions settings) => { settings.BaseAddress = incoming.BaseAddress; settings.PcsAddress = incoming.PcsAddress; settings.Cookie = incoming.Cookie; settings.AccessToken = incoming.AccessToken; settings.AppId = incoming.AppId; settings.ChunkSizeMiB = Math.Clamp(incoming.ChunkSizeMiB, 4, 64); settings.MaxRetries = Math.Max(0, incoming.MaxRetries); return Results.Ok(); });
app.MapRazorPages();
app.MapRazorComponents<BililiveAutoUploader.Web.Components.App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();
app.Run();
public partial class Program;
