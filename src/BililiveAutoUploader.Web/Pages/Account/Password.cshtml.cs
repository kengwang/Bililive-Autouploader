using System.ComponentModel.DataAnnotations;
using BililiveAutoUploader.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BililiveAutoUploader.Web.Pages.Account;

[Authorize]
public sealed class PasswordModel(IPanelCredentialService credentials) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "请输入当前密码。")]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请输入新密码。")]
    [MinLength(12, ErrorMessage = "新密码至少需要 12 个字符。")]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请再次输入新密码。")]
    [Compare(nameof(NewPassword), ErrorMessage = "两次输入的新密码不一致。")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    [TempData]
    public bool Changed { get; set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await credentials.ChangePasswordAsync(CurrentPassword, NewPassword, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case ChangePasswordResult.CurrentPasswordInvalid:
                ModelState.AddModelError(string.Empty, "当前密码不正确。");
                return Page();
            case ChangePasswordResult.NewPasswordInvalid:
                ModelState.AddModelError(string.Empty, "新密码至少需要 12 个字符。");
                return Page();
            case ChangePasswordResult.Success:
                Changed = true;
                return RedirectToPage();
            default:
                throw new InvalidOperationException("未知的密码更新结果。");
        }
    }
}
