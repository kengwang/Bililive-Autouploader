using BililiveAutoUploader.Domain;
using BililiveAutoUploader.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BililiveAutoUploader.Web.Authentication;

public sealed class PanelAuthenticationOptions
{
    public string InitialPassword { get; set; } = string.Empty;
}

public sealed class PanelUser;

public enum ChangePasswordResult
{
    Success,
    CurrentPasswordInvalid,
    NewPasswordInvalid
}

public interface IPanelCredentialService
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken);
    Task<bool> VerifyPasswordAsync(string password, CancellationToken cancellationToken);
    Task<ChangePasswordResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken);
}

public sealed class PanelCredentialService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IPasswordHasher<PanelUser> passwordHasher,
    IOptions<PanelAuthenticationOptions> options) : IPanelCredentialService
{
    private const string PasswordHashKey = "Panel.PasswordHash";
    private const int MinimumPasswordLength = 12;
    private static readonly PanelUser User = new();

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.ApplicationSettings.AnyAsync(x => x.Key == PasswordHashKey, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var initialPassword = options.Value.InitialPassword;
        if (!IsValidPassword(initialPassword))
        {
            throw new InvalidOperationException(
                $"面板尚未初始化。请在首次启动时通过 Panel__InitialPassword 提供至少 {MinimumPasswordLength} 个字符的临时密码；初始化后应立即移除该环境变量。");
        }

        db.ApplicationSettings.Add(new ApplicationSetting
        {
            Key = PasswordHashKey,
            Value = passwordHasher.HashPassword(User, initialPassword)
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VerifyPasswordAsync(string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == PasswordHashKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return false;
        }

        var verification = passwordHasher.VerifyHashedPassword(User, setting.Value, password);
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            setting.Value = passwordHasher.HashPassword(User, password);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return verification != PasswordVerificationResult.Failed;
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (!IsValidPassword(newPassword))
        {
            return ChangePasswordResult.NewPasswordInvalid;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var setting = await db.ApplicationSettings.SingleAsync(x => x.Key == PasswordHashKey, cancellationToken).ConfigureAwait(false);
        if (passwordHasher.VerifyHashedPassword(User, setting.Value, currentPassword) == PasswordVerificationResult.Failed)
        {
            return ChangePasswordResult.CurrentPasswordInvalid;
        }

        setting.Value = passwordHasher.HashPassword(User, newPassword);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ChangePasswordResult.Success;
    }

    private static bool IsValidPassword(string? password) => password?.Length >= MinimumPasswordLength;
}
