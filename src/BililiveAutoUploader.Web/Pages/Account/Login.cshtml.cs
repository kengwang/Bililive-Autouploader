using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BililiveAutoUploader.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace BililiveAutoUploader.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(IPanelCredentialService credentials) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "请输入密码。")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        return User.Identity?.IsAuthenticated == true ? LocalRedirect(GetSafeReturnUrl()) : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!await credentials.VerifyPasswordAsync(Password, cancellationToken).ConfigureAwait(false))
        {
            ModelState.AddModelError(string.Empty, "密码不正确。");
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "panel-admin"),
            new Claim(ClaimTypes.Name, "管理员")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties
        {
            AllowRefresh = true,
            IsPersistent = RememberMe
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties).ConfigureAwait(false);
        return LocalRedirect(GetSafeReturnUrl());
    }

    private string GetSafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";
}
